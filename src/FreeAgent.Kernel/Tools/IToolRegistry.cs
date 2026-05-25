namespace FreeAgent.Kernel;

public interface IToolRegistry
{
    void Register(ITool tool);
    ITool? Find(string name);
    IReadOnlyList<ToolDefinition> Definitions { get; }
}
