namespace FreeAgent.Kernel;

/// <summary>
/// Executes a completed provider tool-call batch according to the kernel concurrency contract.
/// Read-only/concurrency-safe calls run in one parallel window; all other calls run serially.
/// Returned results always preserve the original tool-call order.
/// </summary>
public sealed class TurnExecutor
{
    private readonly IToolRegistry _registry;
    private readonly ToolPipeline _pipeline;

    public TurnExecutor(IToolRegistry registry, ToolPipeline pipeline)
    {
        _registry = registry;
        _pipeline = pipeline;
    }

    public async ValueTask<IReadOnlyList<ToolResult>> ExecuteBatchAsync(
        IReadOnlyList<ToolCall> calls,
        SessionState state,
        CancellationToken cancellationToken)
    {
        var results = new ToolResult?[calls.Count];
        var parallelIndexes = new List<int>();
        var serialIndexes = new List<int>();

        for (var index = 0; index < calls.Count; index++)
        {
            if (CanRunInParallel(calls[index]))
            {
                parallelIndexes.Add(index);
            }
            else
            {
                serialIndexes.Add(index);
            }
        }

        if (parallelIndexes.Count > 0)
        {
            await ExecuteParallelWindowAsync(calls, state, parallelIndexes, results, cancellationToken);
        }

        foreach (var index in serialIndexes)
        {
            results[index] = await ExecuteOneAsync(calls[index], state, cancellationToken, isSiblingAbort: false);
        }

        return results.Select(result => result ?? ToolResult.Crash(
            "Tool execution did not produce a result.",
            "Retry the turn; if this repeats, inspect the turn executor."))
            .ToArray();
    }

    private bool CanRunInParallel(ToolCall call)
    {
        var tool = _registry.Find(call.Name);
        return tool is { IsReadOnly: true, IsConcurrencySafe: true };
    }

    private async Task ExecuteParallelWindowAsync(
        IReadOnlyList<ToolCall> calls,
        SessionState state,
        IReadOnlyList<int> indexes,
        ToolResult?[] results,
        CancellationToken userCancellationToken)
    {
        using var siblingAbort = CancellationTokenSource.CreateLinkedTokenSource(userCancellationToken);
        var tasks = indexes.Select(index => ExecuteParallelOneAsync(
            index,
            calls[index],
            state,
            results,
            siblingAbort,
            userCancellationToken)).ToArray();

        await Task.WhenAll(tasks);
    }

    private async Task ExecuteParallelOneAsync(
        int index,
        ToolCall call,
        SessionState state,
        ToolResult?[] results,
        CancellationTokenSource siblingAbort,
        CancellationToken userCancellationToken)
    {
        var result = await ExecuteOneAsync(
            call,
            state,
            siblingAbort.Token,
            isSiblingAbort: siblingAbort.IsCancellationRequested && !userCancellationToken.IsCancellationRequested);

        if (result.Kind == ToolResultKind.Cancelled
            && siblingAbort.IsCancellationRequested
            && !userCancellationToken.IsCancellationRequested)
        {
            result = ToolResult.Cancelled("Tool execution was cancelled by sibling abort.");
        }

        results[index] = result;

        if (result.Kind == ToolResultKind.Crash && !userCancellationToken.IsCancellationRequested)
        {
            await siblingAbort.CancelAsync();
        }
    }

    private async ValueTask<ToolResult> ExecuteOneAsync(
        ToolCall call,
        SessionState state,
        CancellationToken cancellationToken,
        bool isSiblingAbort)
    {
        try
        {
            var result = await _pipeline.ExecuteAsync(call, state, cancellationToken);
            if (result.Kind == ToolResultKind.Cancelled && isSiblingAbort)
            {
                return ToolResult.Cancelled("Tool execution was cancelled by sibling abort.");
            }

            return result;
        }
        catch (OperationCanceledException) when (isSiblingAbort)
        {
            return ToolResult.Cancelled("Tool execution was cancelled by sibling abort.");
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Cancelled();
        }
        catch (Exception ex)
        {
            return ToolResult.Crash(
                $"Tool '{call.Name}' crashed: {ex.Message}",
                retryHint: "The tool threw an unexpected error. Re-check the arguments, or try a different approach.");
        }
    }
}
