using System.Text.Json;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests;

/// <summary>
/// Permission engine fake for pipeline tests: returns a fixed decision and records
/// how many times <see cref="Decide"/> was invoked, so tests can assert that
/// short-circuit paths skip the permission step entirely.
/// </summary>
public sealed class RecordingPermissionEngine : IPermissionEngine
{
    private readonly PermissionDecision _decision;

    public RecordingPermissionEngine(PermissionDecision? decision = null) =>
        _decision = decision ?? PermissionDecision.Allow();

    public int DecideCallCount { get; private set; }

    public static RecordingPermissionEngine Allowing() => new(PermissionDecision.Allow());
    public static RecordingPermissionEngine Denying(string reason) => new(PermissionDecision.Deny(reason));

    public PermissionDecision Decide(ToolCall call, ITool tool, JsonDocument arguments)
    {
        DecideCallCount++;
        return _decision;
    }
}
