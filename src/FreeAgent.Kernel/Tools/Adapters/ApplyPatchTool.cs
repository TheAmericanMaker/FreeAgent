using System.Text;
using System.Text.Json;

namespace FreeAgent.Kernel;

/// <summary>
/// Applies a unified diff to a single workspace file. Each hunk's removed+context lines are matched
/// in the current file content (must be unique) and replaced with the added+context lines, so the
/// edit is precise. Atomic: if any hunk fails to match (missing or ambiguous), nothing is written.
/// Snapshots for <c>/undo</c>. Use this when the model already has a diff; for ad-hoc edits prefer
/// <see cref="EditFileTool"/> or <see cref="MultiEditFileTool"/>.
/// </summary>
public sealed class ApplyPatchTool : ITool
{
    public string Name => "ApplyPatch";

    public string Description =>
        "Apply a unified diff to a single file. Each hunk's removed + context lines must match the "
        + "file uniquely; if any hunk doesn't match, no changes are written. Required: 'path' (the "
        + "file to patch) and 'patch' (unified-diff text with @@ hunk headers; file headers like "
        + "'---'/'+++' are tolerated but ignored).";

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public JsonDocument InputSchema { get; } = JsonDocument.Parse(
        """{"type":"object","required":["path","patch"],"properties":{"path":{"type":"string"},"patch":{"type":"string"}}}""");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonDocument arguments, ToolContext context) =>
        [new FileWriteCap(ResolvePath(arguments, context))];

    public async ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var path = ResolvePath(arguments, context);
        var patch = arguments.RootElement.GetProperty("patch").GetString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(patch))
            return ToolResult.InvalidInput("'patch' must not be empty.");

        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}");

        var hunks = ParseHunks(patch);
        if (hunks.Count == 0)
            return ToolResult.InvalidInput("No '@@' hunk headers found in patch.");

        string original;
        try
        {
            original = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Failed to read '{path}': {ex.Message}");
        }

        var working = original;
        var index = 0;
        foreach (var (oldText, newText) in hunks)
        {
            if (oldText.Length == 0)
                return ToolResult.InvalidInput($"Hunk #{index} has no content to match.");

            var count = CountOccurrences(working, oldText);
            if (count == 0)
                return ToolResult.InvalidInput($"Hunk #{index} did not match (no changes written).");
            if (count > 1)
                return ToolResult.InvalidInput($"Hunk #{index} is ambiguous: matches {count} places (no changes written).");

            working = ReplaceFirst(working, oldText, newText);
            index++;
        }

        if (working == original)
            return ToolResult.Empty($"No changes to {path} (patch was a no-op).");

        try
        {
            await File.WriteAllTextAsync(path, working, cancellationToken);
            context.Session.History.Record(path, original);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Failed to write '{path}': {ex.Message}");
        }

        return ToolResult.Success($"Applied patch to {path}: {hunks.Count} hunk(s).");
    }

    /// <summary>
    /// Parses unified-diff hunks into <c>(oldText, newText)</c> pairs by category-merging the
    /// <c>' '</c> / <c>'-'</c> / <c>'+'</c> lines. File headers (<c>---</c> / <c>+++</c>) are ignored.
    /// Public for unit-testability.
    /// </summary>
    public static IReadOnlyList<(string OldText, string NewText)> ParseHunks(string patch)
    {
        var hunks = new List<(string, string)>();
        var lines = patch.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            if (!lines[i].StartsWith("@@", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            i++; // skip the @@ header itself
            var oldLines = new List<string>();
            var newLines = new List<string>();
            while (i < lines.Length
                   && !lines[i].StartsWith("@@", StringComparison.Ordinal)
                   && !lines[i].StartsWith("--- ", StringComparison.Ordinal)
                   && !lines[i].StartsWith("+++ ", StringComparison.Ordinal))
            {
                var line = lines[i];
                if (line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("---", StringComparison.Ordinal))
                {
                    oldLines.Add(line[1..]);
                }
                else if (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal))
                {
                    newLines.Add(line[1..]);
                }
                else if (line.StartsWith(" ", StringComparison.Ordinal))
                {
                    oldLines.Add(line[1..]);
                    newLines.Add(line[1..]);
                }
                else if (line.Length == 0)
                {
                    oldLines.Add(string.Empty);
                    newLines.Add(string.Empty);
                }
                // anything else (e.g. "\ No newline at end of file") is ignored
                i++;
            }

            hunks.Add((string.Join('\n', oldLines), string.Join('\n', newLines)));
        }

        return hunks;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string haystack, string needle, string replacement)
    {
        var i = haystack.IndexOf(needle, StringComparison.Ordinal);
        return i < 0 ? haystack : string.Concat(haystack.AsSpan(0, i), replacement, haystack.AsSpan(i + needle.Length));
    }

    private static string ResolvePath(JsonDocument arguments, ToolContext context) =>
        WorkspacePath.Resolve(
            arguments.RootElement.GetProperty("path").GetString()!,
            context.Session.WorkingDirectory);
}
