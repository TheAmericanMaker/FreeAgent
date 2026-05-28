using System.Text.Json;

namespace FreeAgent.Kernel;

/// <summary>
/// Applies a batch of literal-string edits to one file *atomically* — if any edit fails (its
/// <c>old_string</c> isn't found or isn't unique without <c>replace_all</c>), nothing is written.
/// Same unique-match safety as <see cref="EditFileTool"/>, but lets the model make several precise
/// changes in a single tool call (one read, one write) instead of a round-trip per edit.
/// </summary>
public sealed class MultiEditFileTool : ITool
{
    public string Name => "MultiEditFile";

    public string Description =>
        "Apply a batch of in-place string-replacements to one file atomically. Each edit must have "
        + "a unique 'old_string' (or pass 'replace_all': true). If any edit fails, no changes are "
        + "written. Required: 'path' and 'edits' (a non-empty array of { old_string, new_string, "
        + "replace_all? }). Prefer this when you have several precise changes to the same file.";

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public JsonDocument InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type":"object",
          "required":["path","edits"],
          "properties":{
            "path":{"type":"string"},
            "edits":{
              "type":"array",
              "items":{
                "type":"object",
                "required":["old_string","new_string"],
                "properties":{
                  "old_string":{"type":"string"},
                  "new_string":{"type":"string"},
                  "replace_all":{"type":"boolean"}
                }
              }
            }
          }
        }
        """);

    public IReadOnlyList<Capability> RequiredCapabilities(JsonDocument arguments, ToolContext context) =>
        [new FileWriteCap(ResolvePath(arguments, context))];

    public async ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var path = ResolvePath(arguments, context);

        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}");

        var editsEl = arguments.RootElement.GetProperty("edits");
        if (editsEl.ValueKind != JsonValueKind.Array || editsEl.GetArrayLength() == 0)
            return ToolResult.InvalidInput("'edits' must be a non-empty array.");

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
        var totalReplacements = 0;
        var index = 0;
        foreach (var editEl in editsEl.EnumerateArray())
        {
            var oldStr = editEl.GetProperty("old_string").GetString() ?? string.Empty;
            var newStr = editEl.GetProperty("new_string").GetString() ?? string.Empty;
            var replaceAll = editEl.TryGetProperty("replace_all", out var ra) && ra.ValueKind == JsonValueKind.True;

            if (oldStr.Length == 0)
                return ToolResult.InvalidInput($"Edit #{index}: 'old_string' must not be empty.");

            var occurrences = CountOccurrences(working, oldStr);
            if (occurrences == 0)
                return ToolResult.InvalidInput($"Edit #{index}: 'old_string' not found (no changes written).");
            if (occurrences > 1 && !replaceAll)
                return ToolResult.InvalidInput(
                    $"Edit #{index}: 'old_string' occurs {occurrences} times; add context to make it unique, or pass 'replace_all': true (no changes written).");

            working = replaceAll
                ? working.Replace(oldStr, newStr, StringComparison.Ordinal)
                : ReplaceFirst(working, oldStr, newStr);
            totalReplacements += replaceAll ? occurrences : 1;
            index++;
        }

        if (working == original)
            return ToolResult.Empty($"No changes to {path} (all edits were identity replacements).");

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

        return ToolResult.Success($"Edited {path}: {editsEl.GetArrayLength()} edit(s), {totalReplacements} replacement(s).");
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
