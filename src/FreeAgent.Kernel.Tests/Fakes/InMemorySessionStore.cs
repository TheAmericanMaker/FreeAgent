using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests;

public sealed class InMemorySessionStore : IPersistenceStore
{
    public int SaveCount { get; private set; }
    public SessionState? LastSaved { get; private set; }

    public ValueTask SaveAsync(SessionState state, CancellationToken cancellationToken)
    {
        SaveCount++;
        LastSaved = state;
        return ValueTask.CompletedTask;
    }

    public ValueTask<SessionState> LoadAsync(string sessionId, CancellationToken cancellationToken) =>
        LastSaved?.SessionId == sessionId ? ValueTask.FromResult(LastSaved) : throw new InvalidOperationException($"No session {sessionId}");
}
