namespace FreeAgent.Kernel;

/// <summary>
/// A named sub-agent role: the tools it may use and a system-prompt suffix that frames its focus.
/// Sub-agents share the parent's provider, permission engine, and approver — only the tool registry
/// is filtered to the role's allow-list.
/// </summary>
public sealed record AgentDefinition(string Type, IReadOnlyList<string> AllowedTools, string SystemPromptSuffix);

/// <summary>Lookup of <see cref="AgentDefinition"/>s by type name.</summary>
public sealed class AgentRegistry
{
    private readonly Dictionary<string, AgentDefinition> _agents = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Types => _agents.Keys;

    public void Register(AgentDefinition definition) => _agents[definition.Type] = definition;

    public AgentDefinition? Find(string type) => _agents.GetValueOrDefault(type);
}
