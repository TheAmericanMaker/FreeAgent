namespace FreeAgent.Kernel;

public interface IPersistenceStore
{
    ValueTask SaveAsync(SessionState state, CancellationToken cancellationToken);
    ValueTask<SessionState> LoadAsync(string sessionId, CancellationToken cancellationToken);
}
