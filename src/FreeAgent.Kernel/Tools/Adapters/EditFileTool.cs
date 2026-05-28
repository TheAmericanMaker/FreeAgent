using System.Text.Json;

namespace FreeAgent.Kernel;

/// <summary>
/// In-place file edit by literal string replacement, with a uniqueness check by default to make
/// edits safe and intentional. Mutates the file, so it is not read-only and not concurrency-safe;
/// requires a <see cref="FileWriteCap"/> for the resolved path. Use this rather than full-file
/// rewrites: it preserves everything you didn't ask to change and is far cheaper in tokens.
/// </summary>
public sealed class EditFileTool : ITool
{
    public string Name => "EditFile";

    public string Description =>
        "Edit a workspace file in place by replacing a literal substring. By default the substring "
        + "must occur exactly once (so edits are precise); pass 'replace_all: true' to replace every "
        + "occurrence. Required: 'path', 'old_string', 'new_string'. Prefer this over WriteFile for "
        + "changes to existing files — it's safer and uses far fewer tokens than rewriting the whole file.";

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public JsonDocument InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type":"object",
          "required":["path","old_string","new_string"],
          "properties":{
            "path":{"type":"string"},
            "old_string":{"type":"string"},
            "new_string":{"type":"string"},
            "replace_all":{"type":"boolean"}
          }
        }
        """);

    public IReadOnlyList<Capability> RequiredCapabilities(JsonDocument arguments, ToolContext context) =>
        [new FileWriteCap(ResolvePath(arguments, context))];

    public async ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var path = ResolvePath(arguments, context);
        var oldString = arguments.RootElement.GetProperty("old_string").GetString() ?? string.Empty;
        var newString = arguments.RootElement.GetProperty("new_string").GetString() ?? string.Empty;
        var replaceAll = arguments.RootElement.TryGetProperty("replace_all", out var ra) && ra.ValueKind == JsonValueKind.True;

        if (oldString.Length == 0)
            return ToolResult.InvalidInput("'old_string' must not be empty.");

        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}");

        string original;
        try
        {
            original = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Failed to read '{path}': {ex.Message}");
        }

        var occurrences = CountOccurrences(original, oldString);
        if (occurrences == 0)
            return ToolResult.InvalidInput($"'old_string' not found in {path}. Add more surrounding context to make the match unique.");
        if (occurrences > 1 && !replaceAll)
            return ToolResult.InvalidInput(
                $"'old_string' occurs {occurrences} times in {path}; add more surrounding context to make the match unique, or pass 'replace_all': true.");

        if (oldString == newString)
            return ToolResult.Empty($"No changes to {path} (old_string and new_string are identical).");

        var updated = replaceAll
            ? original.Replace(oldString, newString, StringComparison.Ordinal)
            : ReplaceFirst(original, oldString, newString);
        var replacements = replaceAll ? occurrences : 1;

        try
        {
            await File.WriteAllTextAsync(path, updated, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Failed to write '{path}': {ex.Message}");
        }

        return ToolResult.Success($"Edited {path}: {replacements} replacement{(replacements == 1 ? "" : "s")}.");
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
