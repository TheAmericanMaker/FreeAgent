using System.Text.Json;

namespace FreeAgent.Kernel;

public sealed class ToolPipeline
{
    private readonly IToolRegistry _registry;
    private readonly IPermissionEngine _permissions;
    public List<string> StepLog { get; } = [];

    public ToolPipeline(IToolRegistry registry, IPermissionEngine permissions)
    {
        _registry = registry;
        _permissions = permissions;
    }

    public async ValueTask<ToolResult> ExecuteAsync(ToolCall call, SessionState state, CancellationToken cancellationToken)
    {
        StepLog.Add("parse");
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);

        StepLog.Add("schema-validate");
        var tool = _registry.Find(call.Name);
        if (tool is null)
        {
            return ToolResult.Error($"Unknown tool: {call.Name}");
        }

        StepLog.Add("sanity-check");
        StepLog.Add("plan-mode-guard");
        StepLog.Add("permission");
        var decision = _permissions.Decide(call, tool, arguments);
        if (!decision.Allowed)
        {
            return ToolResult.Error($"Permission denied: {decision.Reason}");
        }

        StepLog.Add("cache-lookup");
        StepLog.Add("pre-hook");
        StepLog.Add("execute");
        var result = await tool.ExecuteAsync(arguments, new ToolContext(state), cancellationToken);
        StepLog.Add("post-hook");
        StepLog.Add("artifact-store");
        StepLog.Add("cache-write");
        StepLog.Add("invalidate");
        return result;
    }
}
