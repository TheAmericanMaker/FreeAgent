using System.Text.Json;

namespace FreeAgent.Kernel;

/// <summary>
/// Turns plan mode on. While plan mode is active the pipeline's plan-mode guard blocks every
/// non-read-only tool, so the agent can explore and propose changes without making any. Both plan-mode
/// tools are read-only (so they are never blocked by the guard — in particular
/// <see cref="ExitPlanModeTool"/> must remain callable while plan mode is active) and not
/// concurrency-safe (they mutate shared <see cref="SessionState.PlanMode"/>, so they run serially).
/// They require no capabilities.
/// </summary>
public sealed class EnterPlanModeTool : ITool
{
    public string Name => "EnterPlanMode";
    public string Description =>
        "Enter plan mode: from now on only read-only tools (reading, searching) are allowed and any "
        + "write or command is blocked, so you can investigate and propose a plan safely. Call "
        + "ExitPlanMode when the user approves the plan and you are ready to make changes.";
    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => false;

    public JsonDocument InputSchema { get; } = JsonDocument.Parse("""{"type":"object","properties":{}}""");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonDocument arguments, ToolContext context) => [];

    public ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken)
    {
        context.Session.PlanMode = true;
        return ValueTask.FromResult(ToolResult.Success("Plan mode is now ON. Only read-only tools will run until you call ExitPlanMode."));
    }
}

/// <summary>
/// Turns plan mode off, re-enabling writable tools. Read-only and never blocked by the plan-mode
/// guard, so it is always callable. See <see cref="EnterPlanModeTool"/>.
/// </summary>
public sealed class ExitPlanModeTool : ITool
{
    public string Name => "ExitPlanMode";
    public string Description =>
        "Exit plan mode, re-enabling writable tools (WriteFile, ProcessExec, …). Call this once the "
        + "user has approved your plan and you are ready to make changes.";
    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => false;

    public JsonDocument InputSchema { get; } = JsonDocument.Parse("""{"type":"object","properties":{}}""");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonDocument arguments, ToolContext context) => [];

    public ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken)
    {
        context.Session.PlanMode = false;
        return ValueTask.FromResult(ToolResult.Success("Plan mode is now OFF. Writable tools are enabled again."));
    }
}
