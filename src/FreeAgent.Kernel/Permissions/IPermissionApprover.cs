namespace FreeAgent.Kernel;

/// <summary>The user's response to an interactive permission prompt.</summary>
public enum ApprovalDecision
{
    /// <summary>Reject this call.</summary>
    Deny,

    /// <summary>Allow this one call only.</summary>
    Once,

    /// <summary>Allow this capability for the rest of the session (recorded in <see cref="SessionState.SessionApprovals"/>).</summary>
    Session
}

/// <summary>A request for the user to approve an uncovered capability before a tool runs.</summary>
public sealed record ApprovalRequest(string ToolName, IReadOnlyList<Capability> Capabilities, string Reason);

/// <summary>
/// Frontend seam for interactive permission approval. The tool pipeline consults an approver only
/// when the permission engine returns <see cref="PermissionOutcome.Prompt"/> (an uncovered, but not
/// hard-blocked, capability). With no approver the pipeline treats a prompt as a denial, preserving
/// the kernel's deterministic, non-interactive default. Implementations live in the frontend (e.g.
/// a console prompt in the host); persisting an "always" choice to config is the frontend's concern.
/// </summary>
public interface IPermissionApprover
{
    ValueTask<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken);
}
