namespace FreeAgent.Kernel;

/// <summary>
/// Outcome of a permission evaluation. <see cref="Reason"/> is model-facing; <see cref="RetryHint"/>
/// is an optional suggestion the pipeline forwards to <see cref="ToolResult.PermissionDenied"/>.
/// </summary>
public sealed record PermissionDecision(bool Allowed, string Reason, string? RetryHint = null)
{
    public static PermissionDecision Allow() => new(true, "allowed");

    public static PermissionDecision Deny(string reason, string? retryHint = null) =>
        new(false, reason, retryHint);
}
