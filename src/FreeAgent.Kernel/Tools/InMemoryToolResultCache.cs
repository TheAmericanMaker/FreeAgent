using System.Collections.Concurrent;

namespace FreeAgent.Kernel;

/// <summary>
/// Default in-memory implementation of <see cref="IToolResultCache"/>: a concurrent dictionary, no
/// eviction policy. Sessions are bounded so unbounded growth is acceptable for an MVP; LRU/TTL can
/// layer on later without changing the interface. Thread-safe: read-only tools share a single
/// pipeline instance and run together in the executor's parallel window.
/// </summary>
public sealed class InMemoryToolResultCache : IToolResultCache
{
    private readonly ConcurrentDictionary<string, ToolResult> _entries = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public bool TryGet(string key, out ToolResult result) => _entries.TryGetValue(key, out result!);

    public void Set(string key, ToolResult result) => _entries[key] = result;

    public void InvalidateAll() => _entries.Clear();
}
