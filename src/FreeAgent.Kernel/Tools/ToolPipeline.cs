using System.Text.Json;

namespace FreeAgent.Kernel;

/// <summary>
/// The 12-step per-tool-call pipeline. Every call traverses the steps in strict order;
/// a failure short-circuits before any later side-effecting step runs. Steps that have no
/// kernel-level implementation yet (sanity-check, cache,
/// hooks, artifact-store, invalidate) are explicit no-op seams: they record their place in
/// <see cref="StepLog"/> so the order is observable and the extension point is obvious.
/// See contracts §"Tool Execution Pipeline".
/// </summary>
public sealed class ToolPipeline
{
    private readonly IToolRegistry _registry;
    private readonly IPermissionEngine _permissions;
    private readonly IPermissionApprover? _approver;
    private readonly IToolResultCache? _cache;
    private readonly IHookRunner? _hooks;
    private readonly object _stepLogGate = new();

    /// <summary>Ordered record of the conceptual steps reached during the last call(s).</summary>
    public List<string> StepLog { get; } = [];

    /// <param name="approver">
    /// Optional interactive approver consulted when the engine returns
    /// <see cref="PermissionOutcome.Prompt"/>. When null, a prompt is treated as a denial — the
    /// deterministic, non-interactive default.
    /// </param>
    /// <param name="cache">
    /// Optional result cache. When supplied, read-only tools short-circuit on a cache hit, their
    /// successful results are stored, and a successful mutating tool invalidates the cache. With no
    /// cache, the pipeline behaves as before.
    /// </param>
    /// <param name="hooks">Optional pre/post-tool hook runner. With no runner, the hook seams stay no-ops.</param>
    public ToolPipeline(
        IToolRegistry registry,
        IPermissionEngine permissions,
        IPermissionApprover? approver = null,
        IToolResultCache? cache = null,
        IHookRunner? hooks = null)
    {
        _registry = registry;
        _permissions = permissions;
        _approver = approver;
        _cache = cache;
        _hooks = hooks;
    }

    public async ValueTask<ToolResult> ExecuteAsync(ToolCall call, SessionState state, CancellationToken cancellationToken)
    {
        // Step 1 — parse. Invalid JSON must not escape as an exception.
        AddStep("parse");
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
            // Arguments are then validated against the tool's declared input schema. A schema
            // failure stops before capabilities are gathered or any side effect occurs.
            AddStep("schema-validate");
            var tool = _registry.Find(call.Name);
            if (tool is null)
            {
                return ToolResult.InvalidInput($"Unknown tool: {call.Name}");
            }

            var validation = ToolInputSchemaValidator.Validate(tool.InputSchema, arguments);
            if (!validation.IsValid)
            {
                return ToolResult.InvalidInput($"Invalid arguments for tool '{call.Name}': {validation.Error}");
            }

            var context = new ToolContext(state);

            // Step 3 — sanity-check (path-escape / workspace boundary). Future seam.
            AddStep("sanity-check");

            // Step 4 — plan-mode-guard. While plan mode is active only read-only tools may run;
            // a writable tool is blocked here, before the permission step gathers capabilities
            // or any side effect occurs. The step is logged before the short-circuit so the
            // order stays observable.
            AddStep("plan-mode-guard");
            if (state.PlanMode && !tool.IsReadOnly)
            {
                return ToolResult.PlanModeBlocked(tool.Name);
            }

            // Step 5 — permission. Gather the tool's required capabilities and let the engine
            // decide; an uncovered, denied, or blocked capability stops here before any side effect.
            AddStep("permission");
            var capabilities = tool.RequiredCapabilities(arguments, context);
            var decision = _permissions.Decide(tool, capabilities, state.WorkingDirectory);
            if (decision.Outcome == PermissionOutcome.Deny)
            {
                return ToolResult.PermissionDenied(decision.Reason, decision.RetryHint);
            }

            if (decision.Outcome == PermissionOutcome.Prompt
                && !await IsApprovedAsync(tool, capabilities, state, cancellationToken))
            {
                return ToolResult.PermissionDenied(decision.Reason, decision.RetryHint);
            }

            // Step 6 — cache-lookup. Read-only tools only; a hit short-circuits before execute.
            AddStep("cache-lookup");
            string? cacheKey = null;
            if (_cache is not null && tool.IsReadOnly)
            {
                cacheKey = $"{tool.Name}|{CanonicalizeJson(arguments)}";
                if (_cache.TryGet(cacheKey, out var cached))
                {
                    return cached;
                }
            }

            // Step 7 — pre-hook. Runs configured pre-tool hooks (non-fatal; errors swallowed by the runner).
            AddStep("pre-hook");
            if (_hooks is not null)
                await _hooks.RunPreToolAsync(call.Name, call.ArgumentsJson, cancellationToken);

            // Step 8 — execute. Cancellation and crashes are mapped to result classes here;
            // an exception never escapes the pipeline.
            AddStep("execute");
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

            // Empty-success normalization happens here so the cached value matches what we return.
            if (result.Kind == ToolResultKind.Success && string.IsNullOrWhiteSpace(result.Content))
            {
                result = ToolResult.Empty($"Tool '{call.Name}' completed but produced no output.");
            }

            // Step 9 — post-hook. Runs configured post-tool hooks; result is not modified by hooks.
            AddStep("post-hook");
            if (_hooks is not null)
                await _hooks.RunPostToolAsync(call.Name, call.ArgumentsJson, result, cancellationToken);

            // Step 10 — artifact-store (large Success previews). Future seam.
            AddStep("artifact-store");

            // Step 11 — cache-write: only Success on a read-only tool (skip errors and Empty).
            AddStep("cache-write");
            if (_cache is not null && cacheKey is not null && result.Kind == ToolResultKind.Success)
            {
                _cache.Set(cacheKey, result);
            }

            // Step 12 — invalidate: a successful mutating tool drops every cached read so stale
            // reads can't survive. Conservative; finer per-capability invalidation is future work.
            AddStep("invalidate");
            if (_cache is not null && !tool.IsReadOnly && result.Kind == ToolResultKind.Success)
            {
                _cache.InvalidateAll();
            }

            return result;
        }
    }

    /// <summary>
    /// Resolves an uncovered (<see cref="PermissionOutcome.Prompt"/>) capability set: honours an
    /// existing session grant, otherwise asks the approver. An "allow for session" choice is
    /// recorded per capability type in <see cref="SessionState.SessionApprovals"/>. With no approver
    /// the answer is no, so the call is denied.
    /// </summary>
    private async ValueTask<bool> IsApprovedAsync(
        ITool tool, IReadOnlyList<Capability> capabilities, SessionState state, CancellationToken cancellationToken)
    {
        if (capabilities.Count > 0 && capabilities.All(c => state.SessionApprovals.Contains(c.GetType().Name)))
        {
            return true;
        }

        if (_approver is null)
        {
            return false;
        }

        var decision = await _approver.RequestAsync(
            new ApprovalRequest(tool.Name, capabilities, $"{tool.Name} requires approval"), cancellationToken);

        switch (decision)
        {
            case ApprovalDecision.Session:
                foreach (var capability in capabilities)
                {
                    state.SessionApprovals.Add(capability.GetType().Name);
                }
                return true;
            case ApprovalDecision.Once:
                return true;
            default:
                return false;
        }
    }

    private void AddStep(string step)
    {
        lock (_stepLogGate)
        {
            StepLog.Add(step);
        }
    }

    /// <summary>Stable cache-key form of the arguments — re-serialize so equivalent JSON canonicalises.</summary>
    private static string CanonicalizeJson(JsonDocument arguments) =>
        JsonSerializer.Serialize(arguments.RootElement, JsonOptions.Default);
}
