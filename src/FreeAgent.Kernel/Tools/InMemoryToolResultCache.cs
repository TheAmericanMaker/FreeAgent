namespace FreeAgent.Kernel;

/// <summary>
/// Default in-memory implementation of <see cref="IToolResultCache"/>: a plain dictionary, no
/// eviction policy. Sessions are bounded so unbounded growth is acceptable for an MVP; LRU/TTL can
/// layer on later without changing the interface.
/// </summary>
public sealed class InMemoryToolResultCache : IToolResultCache
{
    private readonly Dictionary<string, ToolResult> _entries = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public bool TryGet(string key, out ToolResult result) => _entries.TryGetValue(key, out result!);

    public void Set(string key, ToolResult result) => _entries[key] = result;

    public void InvalidateAll() => _entries.Clear();
}
