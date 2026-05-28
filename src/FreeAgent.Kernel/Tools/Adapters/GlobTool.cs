using System.Text.Json;

namespace FreeAgent.Kernel;

/// <summary>
/// Lists workspace files whose path matches a glob pattern. Read-only and concurrency-safe. The
/// required capability is a <see cref="FileReadCap"/> for the resolved search root, which the
/// permission engine auto-allows when the root falls inside the working directory; a root outside the
/// workspace therefore requires an explicit allow rule. Results are relative to the search root,
/// sorted, and capped — a noisy pattern can never return an unbounded list.
/// </summary>
public sealed class GlobTool : ITool
{
    private const int MaxResults = 1000;

    public string Name => "Glob";
    public string Description =>
        "Find files in the workspace whose path matches a glob pattern (e.g. '**/*.cs', 'src/**/test_*.py'). "
        + "Use this to locate files by name before reading them. Takes 'pattern' and an optional 'path' "
        + "(search root, relative to the working directory). Returns matching paths, one per line.";
    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonDocument InputSchema { get; } = JsonDocument.Parse(
        """{"type":"object","required":["pattern"],"properties":{"pattern":{"type":"string"},"path":{"type":"string"}}}""");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonDocument arguments, ToolContext context) =>
        [new FileReadCap(ResolveRoot(arguments, context))];

    public ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var root = ResolveRoot(arguments, context);
        var pattern = arguments.RootElement.GetProperty("pattern").GetString() ?? string.Empty;

        if (!Directory.Exists(root))
            return ValueTask.FromResult(ToolResult.Error($"Search root is not a directory: {root}"));

        var regex = WorkspaceSearch.CompileGlob(pattern);
        var matches = new List<string>();
        var truncated = false;

        foreach (var file in WorkspaceSearch.EnumerateFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = WorkspaceSearch.RelativePath(root, file);
            if (!regex.IsMatch(relative))
                continue;

            if (matches.Count == MaxResults)
            {
                truncated = true;
                break;
            }

            matches.Add(relative);
        }

        if (matches.Count == 0)
            return ValueTask.FromResult(ToolResult.Empty($"No files match '{pattern}'."));

        var body = string.Join('\n', matches);
        if (truncated)
            body += $"\n… (truncated at {MaxResults} matches)";

        return ValueTask.FromResult(ToolResult.Success(body));
    }

    private static string ResolveRoot(JsonDocument arguments, ToolContext context)
    {
        var path = arguments.RootElement.TryGetProperty("path", out var p) && p.GetString() is { Length: > 0 } sub
            ? sub
            : ".";
        return WorkspacePath.Resolve(path, context.Session.WorkingDirectory);
    }
}
