using System.Text.Json;
using System.Text.RegularExpressions;

namespace FreeAgent.Kernel;

/// <summary>
/// Shared root/key handling for the cross-session memory tools. Each memory entry is a single
/// markdown file under the user-level memory directory (XDG-aware:
/// <c>$XDG_CONFIG_HOME/freeagent/memory</c>, default <c>~/.config/freeagent/memory</c>). Keys are
/// restricted to <c>[A-Za-z0-9._-]+</c> so they can't escape the root.
/// </summary>
internal static class MemoryStore
{
    private static readonly Regex KeyPattern = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    public static string DefaultRoot()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
            configHome = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return System.IO.Path.Combine(configHome, "freeagent", "memory");
    }

    /// <summary>Returns the absolute path for <paramref name="key"/>, or null if the key is invalid.</summary>
    public static string? ResolvePath(string root, string key)
    {
        if (string.IsNullOrEmpty(key) || !KeyPattern.IsMatch(key))
            return null;
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(root, key + ".md"));
    }
}

/// <summary>
/// Reads a cross-session memory entry by key. Read-only and concurrency-safe; required capability
/// is a <see cref="MemoryCap"/> with operation <c>read</c>, which the permission engine auto-allows.
/// </summary>
public sealed class ReadMemoryTool : ITool
{
    private readonly string _root;

    public ReadMemoryTool(string? rootDirectory = null) => _root = rootDirectory ?? MemoryStore.DefaultRoot();

    public string Name => "ReadMemory";
    public string Description =>
        "Read a cross-session memory entry by key. Use this to recall facts the user has asked you "
        + "to remember (preferences, project conventions, etc.). Required: 'key' "
        + "(alphanumerics, '.', '_', '-' only).";
    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonDocument InputSchema { get; } = JsonDocument.Parse(
        """{"type":"object","required":["key"],"properties":{"key":{"type":"string"}}}""");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonDocument arguments, ToolContext context) =>
        [new MemoryCap(arguments.RootElement.GetProperty("key").GetString() ?? string.Empty, "read")];

    public async ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var key = arguments.RootElement.GetProperty("key").GetString() ?? string.Empty;
        var path = MemoryStore.ResolvePath(_root, key);
        if (path is null)
            return ToolResult.InvalidInput($"Invalid memory key '{key}' (use letters, digits, '.', '_', '-').");

        if (!File.Exists(path))
            return ToolResult.InvalidInput($"Memory not found: '{key}'.");

        try
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            return ToolResult.Success(content);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Failed to read memory '{key}': {ex.Message}");
        }
    }
}

/// <summary>
/// Writes (or overwrites) a cross-session memory entry. Not read-only; required capability is
/// <see cref="MemoryCap"/> with operation <c>write</c>, which the permission engine does *not*
/// auto-allow — the user must approve (interactively or via an allow rule).
/// </summary>
public sealed class WriteMemoryTool : ITool
{
    private readonly string _root;

    public WriteMemoryTool(string? rootDirectory = null) => _root = rootDirectory ?? MemoryStore.DefaultRoot();

    public string Name => "WriteMemory";
    public string Description =>
        "Save (or overwrite) a cross-session memory entry. Use sparingly, only for facts the user "
        + "has asked you to remember between sessions. Required: 'key' (alphanumerics, '.', '_', "
        + "'-' only) and 'content' (markdown).";
    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public JsonDocument InputSchema { get; } = JsonDocument.Parse(
        """{"type":"object","required":["key","content"],"properties":{"key":{"type":"string"},"content":{"type":"string"}}}""");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonDocument arguments, ToolContext context) =>
        [new MemoryCap(arguments.RootElement.GetProperty("key").GetString() ?? string.Empty, "write")];

    public async ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var key = arguments.RootElement.GetProperty("key").GetString() ?? string.Empty;
        var content = arguments.RootElement.GetProperty("content").GetString() ?? string.Empty;
        var path = MemoryStore.ResolvePath(_root, key);
        if (path is null)
            return ToolResult.InvalidInput($"Invalid memory key '{key}' (use letters, digits, '.', '_', '-').");

        try
        {
            Directory.CreateDirectory(_root);
            await File.WriteAllTextAsync(path, content, cancellationToken);
            return ToolResult.Success($"Saved memory '{key}' ({content.Length} chars).");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Failed to write memory '{key}': {ex.Message}");
        }
    }
}
