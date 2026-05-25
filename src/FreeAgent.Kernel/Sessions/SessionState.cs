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
}
