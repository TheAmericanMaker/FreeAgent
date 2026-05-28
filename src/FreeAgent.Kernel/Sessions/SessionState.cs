namespace FreeAgent.Kernel;

public sealed class SessionState
{
    public SessionState(string sessionId, string workingDirectory, DateTimeOffset startedAt)
    {
        SessionId = sessionId;
        WorkingDirectory = workingDirectory;
        StartedAt = startedAt;
    }

    public string SessionId { get; }
    public string WorkingDirectory { get; }
    public DateTimeOffset StartedAt { get; }
    public List<Message> Messages { get; } = [];

    /// <summary>
    /// When true, the tool pipeline blocks any non-read-only tool at the plan-mode guard
    /// (step 4) before it reaches the permission step. Toggled by the plan-mode command /
    /// the EnterPlanMode / ExitPlanMode tools. Defaults to false. In-memory only; not persisted.
    /// </summary>
    public bool PlanMode { get; set; }

    /// <summary>
    /// Capability type names the user approved "for this session" via an interactive prompt. The
    /// pipeline checks this before prompting again, so an approved capability type runs unattended
    /// for the rest of the session. In-memory only; not persisted.
    /// </summary>
    public HashSet<string> SessionApprovals { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Input tokens reported by the provider on the most recent turn (the model's count of what we
    /// sent). Used by the runtime to decide when to compact. In-memory only; not persisted.
    /// </summary>
    public int LastInputTokens { get; set; }

    /// <summary>
    /// Model context-window size in tokens, used together with <see cref="LastInputTokens"/> to
    /// decide compaction. Default 128k; the host may override per provider/model. In-memory only.
    /// </summary>
    public int ContextWindow { get; set; } = 128_000;

    /// <summary>
    /// Pre-write file snapshots from this session — populated by <see cref="WriteFileTool"/> and
    /// <see cref="EditFileTool"/>, drained by the host's <c>/undo</c> command. In-memory only.
    /// </summary>
    public FileHistory History { get; } = new();
}
