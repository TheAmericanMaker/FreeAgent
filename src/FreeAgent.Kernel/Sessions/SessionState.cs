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
}
