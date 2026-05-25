using System.Text.Json;

namespace FreeAgent.Kernel;

public sealed class PermissionEngine : IPermissionEngine
{
    private readonly HashSet<string> _deniedTools = new(StringComparer.Ordinal);

    public void DenyTool(string name) => _deniedTools.Add(name);

    public PermissionDecision Decide(ToolCall call, ITool tool, JsonDocument arguments) =>
        _deniedTools.Contains(call.Name) ? PermissionDecision.Deny($"{call.Name} denied") : PermissionDecision.Allow();
}
