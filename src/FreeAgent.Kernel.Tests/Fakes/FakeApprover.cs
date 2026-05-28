using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests;

/// <summary>Permission approver fake: returns a fixed decision and counts how often it was asked.</summary>
public sealed class FakeApprover : IPermissionApprover
{
    private readonly ApprovalDecision _decision;

    public FakeApprover(ApprovalDecision decision) => _decision = decision;

    public int CallCount { get; private set; }

    public ValueTask<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        CallCount++;
        return ValueTask.FromResult(_decision);
    }
}
