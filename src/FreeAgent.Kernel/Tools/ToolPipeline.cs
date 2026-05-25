using System.Text.Json;

namespace FreeAgent.Kernel;

/// <summary>
/// The 12-step per-tool-call pipeline. Every call traverses the steps in strict order;
/// a failure short-circuits before any later side-effecting step runs. Steps that have no
/// kernel-level implementation yet (schema-validate, sanity-check, plan-mode-guard, cache,
/// hooks, artifact-store, invalidate) are explicit no-op seams: they record their place in
/// <see cref="StepLog"/> so the order is observable and the extension point is obvious.
/// See contracts §"Tool Execution Pipeline".
/// </summary>
public sealed class ToolPipeline
{
    private readonly IToolRegistry _registry;
    private readonly IPermissionEngine _permissions;

    /// <summary>Ordered record of the conceptual steps reached during the last call(s).</summary>
    public List<string> StepLog { get; } = [];

    public ToolPipeline(IToolRegistry registry, IPermissionEngine permissions)
    {
        _registry = registry;
        _permissions = permissions;
    }

    public async ValueTask<ToolResult> ExecuteAsync(ToolCall call, SessionState state, CancellationToken cancellationToken)
    {
        // Step 1 — parse. Invalid JSON must not escape as an exception.
        StepLog.Add("parse");
        JsonDocument arguments;
        try
        {
            arguments = JsonDocument.Parse(call.ArgumentsJson);
        }
        catch (JsonException ex)
        {
            return ToolResult.InvalidInput($"Invalid JSON arguments for tool '{call.Name}': {ex.Message}");
        }

        using (arguments)
        {
            // Step 2 — schema-validate. Resolving the tool (and therefore its schema) happens
            // here; an unknown tool is an input error and must short-circuit before permission.
            // (Real schema validation against tool.InputSchema is a future seam.)
            StepLog.Add("schema-validate");
            var tool = _registry.Find(call.Name);
            if (tool is null)
            {
                return ToolResult.InvalidInput($"Unknown tool: {call.Name}");
            }

            var context = new ToolContext(state);

            // Step 3 — sanity-check (path-escape / workspace boundary). Future seam.
            StepLog.Add("sanity-check");

            // Step 4 — plan-mode-guard (block non-read-only tools in plan mode). Future seam.
            StepLog.Add("plan-mode-guard");

            // Step 5 — permission. Gather the tool's required capabilities and let the engine
            // decide; an uncovered, denied, or blocked capability stops here before any side effect.
            StepLog.Add("permission");
            var capabilities = tool.RequiredCapabilities(arguments, context);
            var decision = _permissions.Decide(tool, capabilities, state.WorkingDirectory);
            if (!decision.Allowed)
            {
                return ToolResult.PermissionDenied(decision.Reason, decision.RetryHint);
            }

            // Step 6 — cache-lookup (read-only tools only). Future seam; a miss is not a failure.
            StepLog.Add("cache-lookup");

            // Step 7 — pre-hook. Future seam; non-fatal.
            StepLog.Add("pre-hook");

            // Step 8 — execute. Cancellation and crashes are mapped to result classes here;
            // an exception never escapes the pipeline.
            StepLog.Add("execute");
            ToolResult result;
            try
            {
                result = await tool.ExecuteAsync(arguments, context, cancellationToken);
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

            // Step 9 — post-hook. Future seam; non-fatal, result not modified.
            StepLog.Add("post-hook");

            // Step 10 — artifact-store (large Success previews). Future seam.
            StepLog.Add("artifact-store");

            // Step 11 — cache-write (read-only Success only). Future seam.
            StepLog.Add("cache-write");

            // Step 12 — invalidate (after mutating tools). Future seam.
            StepLog.Add("invalidate");

            // A tool that succeeds but returns no content is reported as Empty so the model
            // gets a distinct, non-Success signal rather than a blank Success.
            if (result.Kind == ToolResultKind.Success && string.IsNullOrWhiteSpace(result.Content))
            {
                return ToolResult.Empty($"Tool '{call.Name}' completed but produced no output.");
            }

            return result;
        }
    }
}
