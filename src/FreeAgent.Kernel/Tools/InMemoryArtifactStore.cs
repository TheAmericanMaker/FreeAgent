using System.Collections.Concurrent;

namespace FreeAgent.Kernel;

/// <summary>
/// Default in-memory <see cref="IArtifactStore"/>. References are short opaque strings; storage is
/// per-session (the host constructs one and the artifacts go away when the process ends). Thread-safe
/// so the executor's parallel read-only window can offload large results concurrently.
/// </summary>
public sealed class InMemoryArtifactStore : IArtifactStore
{
    private readonly ConcurrentDictionary<string, string> _entries = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public string Store(string content)
    {
        var reference = Guid.NewGuid().ToString("N")[..10];
        _entries[reference] = content;
        return reference;
    }

    public bool TryGet(string reference, out string content) => _entries.TryGetValue(reference, out content!);
}
