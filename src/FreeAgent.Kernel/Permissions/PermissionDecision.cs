namespace FreeAgent.Kernel;

/// <summary>How the permission engine resolved a request.</summary>
public enum PermissionOutcome
{
    /// <summary>Allowed outright (covered by an auto-allow or an allow rule).</summary>
    Allow,

    /// <summary>Denied and not approvable — a hardcoded security block or an explicit deny rule.</summary>
    Deny,

    /// <summary>Not covered by any rule. A UX layer may approve it interactively; with no approver it is denied.</summary>
    Prompt
}

/// <summary>
/// Outcome of a permission evaluation. <see cref="Reason"/> is model-facing; <see cref="RetryHint"/>
/// is an optional suggestion the pipeline forwards to <see cref="ToolResult.PermissionDenied"/>.
/// </summary>
public sealed record PermissionDecision(PermissionOutcome Outcome, string Reason, string? RetryHint = null)
{
    /// <summary>True only for <see cref="PermissionOutcome.Allow"/>.</summary>
    public bool Allowed => Outcome == PermissionOutcome.Allow;

    public static PermissionDecision Allow() => new(PermissionOutcome.Allow, "allowed");

    public static PermissionDecision Deny(string reason, string? retryHint = null) =>
        new(PermissionOutcome.Deny, reason, retryHint);

    /// <summary>Uncovered capability: approvable by a UX layer, otherwise denied.</summary>
    public static PermissionDecision Prompt(string reason, string? retryHint = null) =>
        new(PermissionOutcome.Prompt, reason, retryHint);
}
