using System.Text;
using System.Text.Json;

namespace FreeAgent.Kernel;

/// <summary>
/// Writes UTF-8 text to a workspace file, creating any missing parent directories first. Not
/// read-only and not concurrency-safe. The required capability is a <see cref="FileWriteCap"/> for
/// the resolved absolute path; the permission engine never auto-allows writes, so the call must be
/// covered by an allow rule or blocked outright for protected prefixes.
/// </summary>
public sealed class WriteFileTool : ITool
{
    private readonly IAtomicFileSystem _atomicFs = new LinuxAtomicFileSystem();

    public string Name => "WriteFile";
    public string Description =>
        "Write UTF-8 text to a workspace file, creating any missing parent directories and overwriting "
        + "an existing file. Use this to create or replace a file's full contents. Takes 'path' "
        + "(absolute, or relative to the working directory) and 'content'.";
    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public JsonDocument InputSchema { get; } = JsonDocument.Parse(
        """{"type":"object","required":["path","content"],"properties":{"path":{"type":"string"},"content":{"type":"string"}}}""");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonDocument arguments, ToolContext context) =>
        [new FileWriteCap(ResolvePath(arguments, context))];

    public async ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var path = ResolvePath(arguments, context);
        var content = arguments.RootElement.GetProperty("content").GetString() ?? string.Empty;

        // Snapshot the pre-write content (null if the file did not exist) for /undo support.
        string? previous = null;
        if (File.Exists(path))
        {
            try { previous = await File.ReadAllTextAsync(path, cancellationToken); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Couldn't read; proceed without a snapshot. /undo will simply not see this write.
            }
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await _atomicFs.WriteAllTextAtomicAsync(path, content, cancellationToken);
            context.Session.History.Record(path, previous);

            // WriteAllTextAsync emits UTF-8 with no BOM, so this is the exact on-disk byte count.
            var bytes = Encoding.UTF8.GetByteCount(content);
            return ToolResult.Success($"File written: {path} ({bytes} bytes)");
        }
        catch (OperationCanceledException)
        {
            // Let cancellation flow to the pipeline, which maps it to a Cancelled result.
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Failed to write '{path}': {ex.Message}");
        }
    }

    private static string ResolvePath(JsonDocument arguments, ToolContext context) =>
        WorkspacePath.Resolve(
            arguments.RootElement.GetProperty("path").GetString()!,
            context.Session.WorkingDirectory);
}
