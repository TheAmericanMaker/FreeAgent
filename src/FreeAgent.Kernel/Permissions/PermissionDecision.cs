namespace FreeAgent.Kernel;

public sealed record PermissionDecision(bool Allowed, string Reason)
{
    public static PermissionDecision Allow() => new(true, "allowed");
    public static PermissionDecision Deny(string reason) => new(false, reason);
}
