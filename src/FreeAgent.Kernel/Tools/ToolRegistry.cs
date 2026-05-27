namespace FreeAgent.Kernel;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);

    public IReadOnlyList<ToolDefinition> Definitions => _tools.Values
        .Select(t => new ToolDefinition(t.Name, t.Description, t.InputSchema, t.IsReadOnly, t.IsConcurrencySafe))
        .ToArray();

    public void Register(ITool tool) => _tools.Add(tool.Name, tool);
    public ITool? Find(string name) => _tools.GetValueOrDefault(name);
}
