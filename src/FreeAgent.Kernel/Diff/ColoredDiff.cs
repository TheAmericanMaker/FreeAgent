using System.Text;

namespace FreeAgent.Kernel;

/// <summary>
/// Renders a unified diff between two text blobs with optional ANSI color markup. The diff
/// algorithm is the standard line-level LCS — fine for the typical file-edit previews the host
/// uses it for; not optimized for very large files (those should fall through to a real diff tool).
/// </summary>
/// <remarks>
/// Why kernel-side: the host's <c>/undo</c> preview and any future TUI / VS Code panel that
/// shows what changed wants the same diff representation. Keeping it in the kernel means every
/// frontend renders identically — only the styling (ANSI vs HTML vs a TUI widget) differs.
/// </remarks>
public static class ColoredDiff
{
    /// <summary>ANSI 256-color sequences chosen to match `git diff --color`'s defaults.</summary>
    public static class Ansi
    {
        public const string Reset = "[0m";
        public const string Red = "[31m";
        public const string Green = "[32m";
        public const string Cyan = "[36m";
        public const string Bold = "[1m";
    }

    /// <summary>
    /// Render a unified diff. <paramref name="contextLines"/> is the number of unchanged lines to
    /// show around each change hunk (matching <c>diff -U</c>'s default of 3). When
    /// <paramref name="color"/> is true, ANSI escape sequences are emitted (red for removed,
    /// green for added, cyan for hunk headers).
    /// </summary>
    public static string Render(
        string oldText,
        string newText,
        string oldLabel = "a",
        string newLabel = "b",
        int contextLines = 3,
        bool color = true)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        var ops = LcsDiff(oldLines, newLines);
        if (!ops.Any(o => o.Op != DiffOp.Equal))
            return string.Empty;

        var sb = new StringBuilder();
        var (cBold, cCyan, cRed, cGreen, cReset) = color
            ? (Ansi.Bold, Ansi.Cyan, Ansi.Red, Ansi.Green, Ansi.Reset)
            : ("", "", "", "", "");

        sb.Append(cBold).Append("--- ").Append(oldLabel).Append('\n').Append(cReset);
        sb.Append(cBold).Append("+++ ").Append(newLabel).Append('\n').Append(cReset);

        // Walk hunks: groups of consecutive non-equal ops, with `contextLines` of equal context
        // on either side. Adjacent hunks whose context overlaps merge into one.
        var hunks = BuildHunks(ops, contextLines);
        foreach (var hunk in hunks)
        {
            sb.Append(cCyan)
              .Append("@@ -").Append(hunk.OldStart + 1).Append(',').Append(hunk.OldCount)
              .Append(" +").Append(hunk.NewStart + 1).Append(',').Append(hunk.NewCount)
              .Append(" @@\n")
              .Append(cReset);

            foreach (var op in hunk.Ops)
            {
                switch (op.Op)
                {
                    case DiffOp.Equal:
                        sb.Append(' ').Append(op.Line).Append('\n');
                        break;
                    case DiffOp.Remove:
                        sb.Append(cRed).Append('-').Append(op.Line).Append(cReset).Append('\n');
                        break;
                    case DiffOp.Add:
                        sb.Append(cGreen).Append('+').Append(op.Line).Append(cReset).Append('\n');
                        break;
                }
            }
        }
        return sb.ToString();
    }

    private enum DiffOp { Equal, Remove, Add }

    private readonly record struct DiffEntry(DiffOp Op, string Line);

    private sealed record Hunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<DiffEntry> Ops);

    /// <summary>Splits text into lines without dropping a trailing blank line; preserves content as-is.</summary>
    private static string[] SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        // Normalize CRLF → LF for diff purposes; the renderer doesn't add CRs back, and that
        // matches `git diff`'s default behavior of treating EOL-only changes uniformly.
        var normalized = text.Replace("\r\n", "\n");
        // Don't use Split with RemoveEmptyEntries — a file ending in \n yields an empty trailing
        // entry that we drop here (so "a\n" doesn't count as two lines).
        var lines = normalized.Split('\n');
        return lines.Length > 0 && lines[^1].Length == 0
            ? lines[..^1]
            : lines;
    }

    /// <summary>
    /// Standard LCS line-level diff: build a length table, walk it backward to emit Add/Remove/Equal
    /// ops in order. O(N*M) time and memory — fine for the diff sizes the host produces.
    /// </summary>
    private static List<DiffEntry> LcsDiff(string[] a, string[] b)
    {
        var n = a.Length;
        var m = b.Length;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var ops = new List<DiffEntry>(n + m);
        int ii = 0, jj = 0;
        while (ii < n && jj < m)
        {
            if (a[ii] == b[jj])
            {
                ops.Add(new(DiffOp.Equal, a[ii])); ii++; jj++;
            }
            else if (lcs[ii + 1, jj] >= lcs[ii, jj + 1])
            {
                ops.Add(new(DiffOp.Remove, a[ii])); ii++;
            }
            else
            {
                ops.Add(new(DiffOp.Add, b[jj])); jj++;
            }
        }
        while (ii < n) ops.Add(new(DiffOp.Remove, a[ii++]));
        while (jj < m) ops.Add(new(DiffOp.Add, b[jj++]));
        return ops;
    }

    private static List<Hunk> BuildHunks(List<DiffEntry> ops, int context)
    {
        var hunks = new List<Hunk>();
        var i = 0;
        var oldLine = 0;
        var newLine = 0;
        while (i < ops.Count)
        {
            if (ops[i].Op == DiffOp.Equal) { oldLine++; newLine++; i++; continue; }

            // Find the change cluster, then extend backward by `context` and forward by `context`.
            var clusterStart = i;
            while (i < ops.Count && ops[i].Op != DiffOp.Equal) i++;
            // Trailing context, capped by the next cluster or end.
            var trailing = 0;
            while (i < ops.Count && ops[i].Op == DiffOp.Equal && trailing < context)
            {
                trailing++;
                i++;
            }
            var clusterEnd = i;

            // Compute backward context (and merge with the previous hunk if it overlaps).
            var hunkStart = clusterStart;
            var leadingContext = 0;
            while (hunkStart > 0 && ops[hunkStart - 1].Op == DiffOp.Equal && leadingContext < context)
            {
                hunkStart--; leadingContext++;
            }

            // Compute oldStart / newStart for this hunk by re-walking from the prior hunk's end.
            // Simpler: replay all ops up to hunkStart to track positions.
            int oldStart = 0, newStart = 0;
            for (var k = 0; k < hunkStart; k++)
            {
                switch (ops[k].Op)
                {
                    case DiffOp.Equal: oldStart++; newStart++; break;
                    case DiffOp.Remove: oldStart++; break;
                    case DiffOp.Add: newStart++; break;
                }
            }

            // Count old/new lines spanned by the hunk.
            int oldCount = 0, newCount = 0;
            var entries = new List<DiffEntry>(clusterEnd - hunkStart);
            for (var k = hunkStart; k < clusterEnd; k++)
            {
                entries.Add(ops[k]);
                switch (ops[k].Op)
                {
                    case DiffOp.Equal: oldCount++; newCount++; break;
                    case DiffOp.Remove: oldCount++; break;
                    case DiffOp.Add: newCount++; break;
                }
            }

            // Merge into previous hunk if its end touches our start.
            if (hunks.Count > 0)
            {
                var prev = hunks[^1];
                var prevEnd = prev.OldStart + prev.OldCount;
                if (prevEnd >= oldStart)
                {
                    // Overlap — concatenate ops, recompute counts.
                    var combined = prev.Ops.Concat(entries).ToList();
                    var oldEnd = Math.Max(prevEnd, oldStart + oldCount);
                    var newEnd = Math.Max(prev.NewStart + prev.NewCount, newStart + newCount);
                    hunks[^1] = new Hunk(prev.OldStart, oldEnd - prev.OldStart, prev.NewStart, newEnd - prev.NewStart, combined);
                    oldLine = oldEnd; newLine = newEnd;
                    continue;
                }
            }

            hunks.Add(new Hunk(oldStart, oldCount, newStart, newCount, entries));
            oldLine = oldStart + oldCount;
            newLine = newStart + newCount;
        }
        return hunks;
    }
}
