namespace FreeAgent.Kernel;

/// <summary>
/// Builds and runs a sub-session for a named agent role: filters the parent registry to the role's
/// allow-list, makes a fresh <see cref="ToolPipeline"/> against that filtered registry (reusing the
/// parent's permission engine, approver, cache, and hooks), constructs an in-memory
/// <see cref="SessionState"/> seeded with the role's system-prompt suffix, and runs one turn.
/// Returns the sub-session's final assistant text. Persistence is no-op and events are silenced so
/// sub-agent activity doesn't leak into the parent's console.
/// </summary>
public sealed class SubAgentRunner
{
    private readonly IProvider _provider;
    private readonly IToolRegistry _parentRegistry;
    private readonly IPermissionEngine _permissions;
    private readonly AgentRegistry _agents;
    private readonly IPermissionApprover? _approver;
    private readonly IHookRunner? _hooks;
    private readonly IToolResultCache? _cache;

    public SubAgentRunner(
        IProvider provider,
        IToolRegistry parentRegistry,
        IPermissionEngine permissions,
        AgentRegistry agents,
        IPermissionApprover? approver = null,
        IHookRunner? hooks = null,
        IToolResultCache? cache = null)
    {
        _provider = provider;
        _parentRegistry = parentRegistry;
        _permissions = permissions;
        _agents = agents;
        _approver = approver;
        _hooks = hooks;
        _cache = cache;
    }

    public async ValueTask<string> RunAsync(string type, string task, SessionState parentSession, CancellationToken cancellationToken)
    {
        var definition = _agents.Find(type)
            ?? throw new ArgumentException($"Unknown agent type '{type}'. Available: {string.Join(", ", _agents.Types)}.");

        var subRegistry = new ToolRegistry();
        foreach (var name in definition.AllowedTools)
        {
            if (_parentRegistry.Find(name) is { } tool)
                subRegistry.Register(tool);
        }

        var subPipeline = new ToolPipeline(subRegistry, _permissions, _approver, _cache, _hooks);
        var subState = new SessionState(
            $"sub-{Guid.NewGuid().ToString("N")[..8]}",
            parentSession.WorkingDirectory,
            DateTimeOffset.UtcNow);

        if (!string.IsNullOrWhiteSpace(definition.SystemPromptSuffix))
            subState.Messages.Add(new Message(MessageRole.System, definition.SystemPromptSuffix));

        var subRuntime = new SessionRuntime(
            _provider, subRegistry, subPipeline, new NoOpPersistenceStore(), new NullEventSink(), subState);

        var result = await subRuntime.RunTurnAsync(task, cancellationToken);
        return result.FinalText;
    }
}
