namespace FreeAgent.Kernel;

/// <summary>
/// The documented tool-result taxonomy. Every class except <see cref="Success"/>
/// is an error (<see cref="ToolResult.IsError"/> is true).
/// Mirrors the "Tool result return shapes" architecture section.
/// </summary>
public enum ToolResultKind
{
    /// <summary>Normal completion with non-empty content.</summary>
    Success,

    /// <summary>Schema, sanity, or unknown-tool rejection of the call's input.</summary>
    InvalidInput,

    /// <summary>Explicit permission denial. May carry a retry hint.</summary>
    PermissionDenied,

    /// <summary>
    /// Plan mode is active and a non-read-only tool was called. Distinct from
    /// <see cref="PermissionDenied"/>: it is raised at the plan-mode guard (pipeline step 4),
    /// before capabilities are gathered, so it never consults the permission engine.
    /// </summary>
    PlanModeBlocked,

    /// <summary>Optimistic-concurrency conflict; carries a retry hint.</summary>
    StateConflict,

    /// <summary>Unhandled exception inside the tool body; carries a retry hint.</summary>
    Crash,

    /// <summary>Tool completed successfully but produced no output.</summary>
    Empty,

    /// <summary><see cref="OperationCanceledException"/> was caught.</summary>
    Cancelled
}

/// <summary>
/// Result of running a tool through the pipeline. <see cref="Content"/> is always the
/// string handed back to the model, regardless of class. <see cref="RetryHint"/> is an
/// optional, model-facing suggestion for how to recover from an error class.
/// </summary>
public sealed record ToolResult(ToolResultKind Kind, string Content, string? RetryHint = null)
{
    /// <summary>True for every class except <see cref="ToolResultKind.Success"/>.</summary>
    public bool IsError => Kind != ToolResultKind.Success;

    public static ToolResult Success(string content) => new(ToolResultKind.Success, content);

    /// <summary>Alias for <see cref="InvalidInput"/>; the documented factory name.</summary>
    public static ToolResult Error(string content) => new(ToolResultKind.InvalidInput, content);

    public static ToolResult InvalidInput(string content) => new(ToolResultKind.InvalidInput, content);

    public static ToolResult PermissionDenied(string content, string? retryHint = null) =>
        new(ToolResultKind.PermissionDenied, content, retryHint);

    /// <summary>
    /// Plan-mode guard rejection for the non-read-only <paramref name="toolName"/>. The
    /// mandated wording lives here so it has a single source of truth (architecture §"/plan").
    /// </summary>
    public static ToolResult PlanModeBlocked(string toolName) =>
        new(ToolResultKind.PlanModeBlocked,
            $"Plan mode is active — only read-only tools are allowed. Call ExitPlanMode first to make changes with {toolName}.",
            $"Call ExitPlanMode first to make changes with {toolName}.");

    public static ToolResult StateConflict(string content, string retryHint) =>
        new(ToolResultKind.StateConflict, content, retryHint);

    public static ToolResult Crash(string content, string retryHint) =>
        new(ToolResultKind.Crash, content, retryHint);

    public static ToolResult Empty(string content) => new(ToolResultKind.Empty, content);

    public static ToolResult Cancelled(string content = "Tool execution was cancelled.") =>
        new(ToolResultKind.Cancelled, content);
}
