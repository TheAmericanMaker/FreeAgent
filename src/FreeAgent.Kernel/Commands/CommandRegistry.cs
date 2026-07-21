namespace FreeAgent.Kernel;

/// <summary>
/// Named, dispatchable command with metadata. A single source of truth for both keybindings and a
/// future fuzzy command palette (the <c>ctrl+p</c>-style ui opencode and zed expose). The host's
/// existing slash-command dispatch can register every command here; downstream clients (TUI,
/// editor extensions) consume the same registry rather than each maintaining their own command
/// table.
/// </summary>
public sealed record CommandDefinition(
    /// <summary>Stable id, in <c>group.action</c> form (e.g. <c>session.fork</c>, <c>plan.toggle</c>).</summary>
    string Id,
    /// <summary>Short human label for the palette ("Fork session", "Toggle plan mode").</summary>
    string Label,
    /// <summary>Optional one-liner shown under the label / in help text.</summary>
    string? Description = null,
    /// <summary>Optional keybinding hint shown in the palette (e.g. "Ctrl+P"). Display-only — bindings are the client's job.</summary>
    string? Shortcut = null,
    /// <summary>Logical grouping for palette sorting / sectioning ("Session", "Editing", "Diagnostics", …).</summary>
    string? Category = null);

/// <summary>
/// In-memory <see cref="CommandDefinition"/> catalog. Pure (no I/O). Designed for additive
/// registration at startup — duplicate ids overwrite (last-write-wins) so a frontend can override
/// labels without forking the kernel-side registration. <see cref="Search"/> is a tiny subsequence
/// matcher (case-insensitive) that's good enough for a fuzzy palette without pulling in a
/// dedicated scoring library.
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, CommandDefinition> _commands = new(StringComparer.Ordinal);

    /// <summary>Register or replace a command by <see cref="CommandDefinition.Id"/>.</summary>
    public CommandRegistry Register(CommandDefinition command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Id);
        _commands[command.Id] = command;
        return this;
    }

    /// <summary>Look up by id; null if missing.</summary>
    public CommandDefinition? TryGet(string id) =>
        _commands.TryGetValue(id, out var c) ? c : null;

    /// <summary>All commands, sorted by category then label for predictable palette ordering.</summary>
    public IReadOnlyList<CommandDefinition> All =>
        [.. _commands.Values
            .OrderBy(c => c.Category ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(c => c.Label, StringComparer.Ordinal)];

    /// <summary>
    /// Subsequence fuzzy match against <see cref="CommandDefinition.Id"/> + <see cref="CommandDefinition.Label"/>.
    /// Case-insensitive; an empty query returns every command in the default order. The matcher
    /// scores by how tightly the query characters land — a contiguous substring beats a wider
    /// spread — so "fk" preferring "Fork" over "Find" is the natural result.
    /// </summary>
    public IReadOnlyList<CommandDefinition> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return All;
        var q = query.Trim();

        var ranked = new List<(int Score, CommandDefinition Command)>(_commands.Count);
        foreach (var cmd in _commands.Values)
        {
            var labelScore = FuzzyScore(q, cmd.Label);
            var idScore = FuzzyScore(q, cmd.Id);
            var score = Math.Min(labelScore, idScore);
            if (score == int.MaxValue) continue;
            ranked.Add((score, cmd));
        }
        return [.. ranked
            .OrderBy(r => r.Score)
            .ThenBy(r => r.Command.Label, StringComparer.Ordinal)
            .Select(r => r.Command)];
    }

    /// <summary>
    /// Subsequence score: <c>int.MaxValue</c> if <paramref name="query"/> isn't a subsequence of
    /// <paramref name="haystack"/>; otherwise <c>(last - first)</c>, so a tighter cluster wins.
    /// Both inputs are matched case-insensitively.
    /// </summary>
    public static int FuzzyScore(string query, string haystack)
    {
        if (haystack is null) return int.MaxValue;
        var qi = 0;
        var first = -1;
        var last = -1;
        for (var i = 0; i < haystack.Length && qi < query.Length; i++)
        {
            if (char.ToLowerInvariant(haystack[i]) == char.ToLowerInvariant(query[qi]))
            {
                if (first < 0) first = i;
                last = i;
                qi++;
            }
        }
        return qi == query.Length ? (last - first) : int.MaxValue;
    }
}
