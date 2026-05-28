namespace FreeAgent.Kernel;

/// <summary>
/// Backs the tool pipeline's <c>cache-lookup</c> / <c>cache-write</c> / <c>invalidate</c> seams.
/// Only read-only tools' successful results are cached; any mutating tool that succeeds invalidates
/// the cache (conservatively, all of it — finer-grained invalidation by capability is a future
/// refinement). Optional: with no cache wired in, the pipeline behaves as before.
/// </summary>
public interface IToolResultCache
{
    /// <summary>Returns true and the cached result if <paramref name="key"/> has been written.</summary>
    bool TryGet(string key, out ToolResult result);

    /// <summary>Stores a result under <paramref name="key"/>.</summary>
    void Set(string key, ToolResult result);

    /// <summary>Drops every entry; called when a mutating tool succeeds.</summary>
    void InvalidateAll();
}
