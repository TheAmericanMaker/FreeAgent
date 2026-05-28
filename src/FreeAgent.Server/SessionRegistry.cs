using System.Collections.Concurrent;
using FreeAgent.Kernel;

namespace FreeAgent.Server;

/// <summary>
/// Tracks live <see cref="SessionRuntime"/>s for the protocol server. Sessions are created lazily
/// per id and disposed on explicit DELETE. In-memory only — durability is the kernel's job (each
/// runtime owns its own <see cref="JsonlSessionStore"/>), and per-process restart of the server
/// drops the in-memory map (the disk files survive). Concurrent by design so multiple parallel
/// requests on the same id (e.g. one POST /turns while a GET /state arrives) don't tear the
/// registry; the per-session runtime serializes its own work.
/// </summary>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();

    public sealed record SessionEntry(SessionState State, SessionRuntime Runtime, string WorkingDirectory);

    public SessionEntry GetOrAdd(string id, Func<string, SessionEntry> factory) =>
        _sessions.GetOrAdd(id, factory);

    public bool TryGet(string id, out SessionEntry entry) =>
        _sessions.TryGetValue(id, out entry!);

    public bool Remove(string id, out SessionEntry entry) =>
        _sessions.TryRemove(id, out entry!);

    public IReadOnlyCollection<string> SessionIds() => [.. _sessions.Keys];
}
