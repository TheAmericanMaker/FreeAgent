using System.Text.Json;
using System.Text.RegularExpressions;

namespace FreeAgent.Kernel;

/// <summary>
/// Searches workspace file contents for a regular expression and returns matching lines as
/// <c>path:line:text</c>. Read-only and concurrency-safe. Like <see cref="GlobTool"/>, the required
/// capability is a <see cref="FileReadCap"/> for the resolved search root, auto-allowed inside the
/// working directory. Binary files (detected by a NUL byte) are skipped, and results are capped so a
/// broad pattern can never return an unbounded payload.
/// </summary>
public sealed class GrepTool : ITool
{
    private const int MaxMatches = 200;

    public string Name => "Grep";
    public string Description =>
        "Search file contents in the workspace with a regular expression and return matching lines as "
        + "'path:line:text'. Use this to find where something is defined or used. Takes 'pattern' (regex), "
        + "optional 'path' (search root), optional 'glob' (file filter like '*.cs'), and optional "
        + "'ignore_case'. Binary files are skipped; results are capped.";
    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonDocument InputSchema { get; } = JsonDocument.Parse(
        """{"type":"object","required":["pattern"],"properties":{"pattern":{"type":"string"},"path":{"type":"string"},"glob":{"type":"string"},"ignore_case":{"type":"boolean"}}}""");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonDocument arguments, ToolContext context) =>
        [new FileReadCap(ResolveRoot(arguments, context))];

    public async ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var root = ResolveRoot(arguments, context);
        var patternText = arguments.RootElement.GetProperty("pattern").GetString() ?? string.Empty;
        var ignoreCase = arguments.RootElement.TryGetProperty("ignore_case", out var ic) && ic.ValueKind == JsonValueKind.True;
        var globFilter = arguments.RootElement.TryGetProperty("glob", out var g) && g.GetString() is { Length: > 0 } gp
            ? WorkspaceSearch.CompileGlob(gp)
            : null;

        if (!Directory.Exists(root))
            return ToolResult.Error($"Search root is not a directory: {root}");

        Regex pattern;
        try
        {
            var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            pattern = new Regex(patternText, options);
        }
        catch (ArgumentException ex)
        {
            return ToolResult.InvalidInput($"Invalid regex '{patternText}': {ex.Message}");
        }

        var matches = new List<string>();
        var truncated = false;

        foreach (var file in WorkspaceSearch.EnumerateFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = WorkspaceSearch.RelativePath(root, file);
            if (globFilter is not null && !globFilter.IsMatch(relative))
                continue;

            string text;
            try
            {
                text = await File.ReadAllTextAsync(file, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (text.Contains('\0'))
                continue; // binary file

            var lineNumber = 0;
            foreach (var line in text.Split('\n'))
            {
                lineNumber++;
                if (!pattern.IsMatch(line))
                    continue;

                if (matches.Count == MaxMatches)
                {
                    truncated = true;
                    break;
                }

                matches.Add($"{relative}:{lineNumber}:{line.TrimEnd('\r')}");
            }

            if (truncated)
                break;
        }

        if (matches.Count == 0)
            return ToolResult.Empty($"No matches for '{patternText}'.");

        var body = string.Join('\n', matches);
        if (truncated)
            body += $"\n… (truncated at {MaxMatches} matches)";

        return ToolResult.Success(body);
    }

    private static string ResolveRoot(JsonDocument arguments, ToolContext context)
    {
        var path = arguments.RootElement.TryGetProperty("path", out var p) && p.GetString() is { Length: > 0 } sub
            ? sub
            : ".";
        return WorkspacePath.Resolve(path, context.Session.WorkingDirectory);
    }
}
