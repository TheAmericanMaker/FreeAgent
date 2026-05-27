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
    private readonly object _stepLogGate = new();

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
            if (!decision.Allowed)
            {
                return ToolResult.PermissionDenied(decision.Reason, decision.RetryHint);
            }

            // Step 6 — cache-lookup (read-only tools only). Future seam; a miss is not a failure.
            AddStep("cache-lookup");

            // Step 7 — pre-hook. Future seam; non-fatal.
            AddStep("pre-hook");

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

            // Step 9 — post-hook. Future seam; non-fatal, result not modified.
            AddStep("post-hook");

            // Step 10 — artifact-store (large Success previews). Future seam.
            AddStep("artifact-store");

            // Step 11 — cache-write (read-only Success only). Future seam.
            AddStep("cache-write");

            // Step 12 — invalidate (after mutating tools). Future seam.
            AddStep("invalidate");

            // A tool that succeeds but returns no content is reported as Empty so the model
            // gets a distinct, non-Success signal rather than a blank Success.
            if (result.Kind == ToolResultKind.Success && string.IsNullOrWhiteSpace(result.Content))
            {
                return ToolResult.Empty($"Tool '{call.Name}' completed but produced no output.");
            }

            return result;
        }
    }

    private void AddStep(string step)
    {
        lock (_stepLogGate)
        {
            StepLog.Add(step);
        }
    }
}
