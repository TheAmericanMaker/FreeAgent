using System.Text.Json;

namespace FreeAgent.Kernel;

public interface IPermissionEngine
{
    PermissionDecision Decide(ToolCall call, ITool tool, JsonDocument arguments);
}
