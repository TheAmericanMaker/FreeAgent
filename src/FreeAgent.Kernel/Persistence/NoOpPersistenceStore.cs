namespace FreeAgent.Kernel;

/// <summary>
/// <see cref="IPersistenceStore"/> that intentionally does nothing — used for sub-agents and other
/// short-lived sessions whose transcript should not be persisted to disk. <see cref="LoadAsync"/>
/// throws because nothing is ever stored.
/// </summary>
public sealed class NoOpPersistenceStore : IPersistenceStore
{
    public ValueTask SaveAsync(SessionState state, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<SessionState> LoadAsync(string sessionId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("NoOpPersistenceStore does not store sessions; nothing to load.");
}
