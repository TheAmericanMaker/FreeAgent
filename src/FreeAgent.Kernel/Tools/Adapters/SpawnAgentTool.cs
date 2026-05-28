using System.Text.Json;

namespace FreeAgent.Kernel;

/// <summary>
/// Spawns a sub-agent for a focused sub-task. Required capability is an <see cref="AgentSpawnCap"/>
/// which the engine does not auto-allow, so the user (or an allow rule) must approve each spawn —
/// sub-agent invocations should be deliberate. Returns the sub-agent's final assistant text.
/// </summary>
public sealed class SpawnAgentTool : ITool
{
    private readonly SubAgentRunner _runner;
    private readonly AgentRegistry _agents;

    public SpawnAgentTool(SubAgentRunner runner, AgentRegistry agents)
    {
        _runner = runner;
        _agents = agents;
    }

    public string Name => "SpawnAgent";

    public string Description =>
        $"Spawn a focused sub-agent for a sub-task. Available types: {string.Join(", ", _agents.Types)}. "
        + "Each type has a restricted tool set (e.g. Explore is read-only, Coder can write). The "
        + "sub-agent runs one turn against 'task' and returns its final text. Required: 'type', 'task'.";

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public JsonDocument InputSchema { get; } = JsonDocument.Parse(
        """{"type":"object","required":["type","task"],"properties":{"type":{"type":"string"},"task":{"type":"string"}}}""");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonDocument arguments, ToolContext context) =>
    [
        new AgentSpawnCap(
            arguments.RootElement.GetProperty("type").GetString() ?? string.Empty,
            arguments.RootElement.GetProperty("task").GetString() ?? string.Empty)
    ];

    public async ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var type = arguments.RootElement.GetProperty("type").GetString() ?? string.Empty;
        var task = arguments.RootElement.GetProperty("task").GetString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(task))
            return ToolResult.InvalidInput("'task' must not be empty.");

        try
        {
            var text = await _runner.RunAsync(type, task, context.Session, cancellationToken);
            return string.IsNullOrWhiteSpace(text)
                ? ToolResult.Empty($"Sub-agent '{type}' produced no output.")
                : ToolResult.Success(text);
        }
        catch (ArgumentException ex)
        {
            return ToolResult.InvalidInput(ex.Message);
        }
    }
}
