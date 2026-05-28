namespace FreeAgent.Host;

/// <summary>
/// Watches the working directory for external modifications and accumulates changed paths so the
/// REPL can tell the model "these files moved under you while you were thinking." Opt-in via
/// <c>FREE_WATCH_FILES=1</c> — disabled by default because <see cref="FileSystemWatcher"/>'s
/// inotify usage on large monorepos can hit kernel limits. Filters noise directories the search
/// tools also skip (<c>.git</c> / <c>node_modules</c> / <c>bin</c> / <c>obj</c> / <c>.vs</c> /
/// <c>.idea</c>) so dependency churn doesn't drown the signal. Path deduplication uses a set, so
/// the typical "save fires multiple FSW events" pattern collapses to one entry per drain.
/// </summary>
public sealed class WorkspaceFileWatcher : IDisposable
{
    private static readonly HashSet<string> NoiseDirectories = new(StringComparer.Ordinal)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea"
    };

    private readonly FileSystemWatcher _watcher;
    private readonly object _gate = new();
    private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
    private readonly string _rootFull;

    public WorkspaceFileWatcher(string workingDirectory)
    {
        _rootFull = Path.GetFullPath(workingDirectory);
        _watcher = new FileSystemWatcher(_rootFull)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += (s, e) => Record(e.FullPath);
        _watcher.EnableRaisingEvents = true;
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Record(e.FullPath);

    /// <summary>
    /// Records a change, filtering noise dirs. Pure (no I/O) so the swallowing is cheap. Public so
    /// tests can inject a synthetic event without racing the OS, and so adapters that feed events
    /// from a different source (e.g. inotify direct) can reuse the watcher's dedupe + noise filter.
    /// </summary>
    public void Record(string fullPath)
    {
        if (IsNoise(fullPath)) return;
        lock (_gate) _changed.Add(fullPath);
    }

    private static bool IsNoise(string path)
    {
        foreach (var segment in path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            if (NoiseDirectories.Contains(segment))
                return true;
        return false;
    }

    /// <summary>
    /// Returns the set of paths changed since the last drain (or since construction) and resets the
    /// buffer. Sorted for deterministic output. Paths are relative to <see cref="WorkingDirectory"/>
    /// using <c>/</c> separators, matching the convention of <c>Glob</c> / <c>Grep</c>.
    /// </summary>
    public IReadOnlyList<string> Drain()
    {
        string[] snapshot;
        lock (_gate)
        {
            snapshot = new string[_changed.Count];
            _changed.CopyTo(snapshot);
            _changed.Clear();
        }
        Array.Sort(snapshot, StringComparer.Ordinal);
        for (var i = 0; i < snapshot.Length; i++)
            snapshot[i] = Path.GetRelativePath(_rootFull, snapshot[i]).Replace(Path.DirectorySeparatorChar, '/');
        return snapshot;
    }

    /// <summary>
    /// Renders a notice line (or null when no changes) suitable for prepending to a user turn. Caps
    /// the visible list at <paramref name="cap"/> entries, summarizing the overflow as "…(N more)".
    /// Pure — composes only the strings.
    /// </summary>
    public static string? RenderNotice(IReadOnlyList<string> changedRelative, int cap = 10)
    {
        if (changedRelative.Count == 0) return null;
        var shown = changedRelative.Count <= cap
            ? changedRelative
            : changedRelative.Take(cap).ToList();
        var bullets = string.Join("\n  - ", shown);
        var more = changedRelative.Count > cap
            ? $"\n  - …({changedRelative.Count - cap} more)"
            : "";
        return $"[freeagent] Files changed externally since the last turn:\n  - {bullets}{more}";
    }

    /// <summary>The working directory the watcher is attached to.</summary>
    public string WorkingDirectory => _rootFull;

    public void Dispose() => _watcher.Dispose();
}
