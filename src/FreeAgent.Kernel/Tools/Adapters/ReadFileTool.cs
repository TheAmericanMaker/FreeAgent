using System.Text.Json;

namespace FreeAgent.Kernel;

/// <summary>
/// Reads a UTF-8 text file from the workspace. Read-only and concurrency-safe. The required
/// capability is a <see cref="FileReadCap"/> for the resolved absolute path, which the permission
/// engine auto-allows when it falls inside the working directory. The full file content is returned;
/// the 50,000-char artifact threshold is handled by a later pipeline step (a future seam), not here.
/// </summary>
public sealed class ReadFileTool : ITool
{
    public string Name => "ReadFile";
    public string Description =>
        "Read a UTF-8 text file from the workspace and return its full contents. Use this to inspect "
        + "source, config, or data files before editing or reasoning about them. Takes a 'path' "
        + "(absolute, or relative to the working directory).";
    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonDocument InputSchema { get; } = JsonDocument.Parse(
        """{"type":"object","required":["path"],"properties":{"path":{"type":"string"}}}""");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonDocument arguments, ToolContext context) =>
        [new FileReadCap(ResolvePath(arguments, context))];

    public async ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var path = ResolvePath(arguments, context);

        if (Directory.Exists(path))
        {
            return ToolResult.Error($"Path is a directory, not a file: {path}");
        }

        if (!File.Exists(path))
        {
            return ToolResult.Error($"File not found: {path}");
        }

        try
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            return ToolResult.Success(content);
        }
        catch (OperationCanceledException)
        {
            // Let cancellation flow to the pipeline, which maps it to a Cancelled result.
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Failed to read '{path}': {ex.Message}");
        }
    }

    private static string ResolvePath(JsonDocument arguments, ToolContext context) =>
        WorkspacePath.Resolve(
            arguments.RootElement.GetProperty("path").GetString()!,
            context.Session.WorkingDirectory);
}
