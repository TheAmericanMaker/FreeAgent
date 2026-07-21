using System.Text.Json;
using FreeAgent.Kernel;

namespace FreeAgent.Host;

/// <summary>
/// Directory-level "workspace trust". A project's <c>.freeagent/config.json</c> can run code on
/// launch — SessionStart/pre/post <b>hooks</b> (via <c>bash -c</c>) and <b>MCP</b>/<b>LSP</b> server
/// processes — and can <b>grant</b> the agent extra privileges (allow rules). That content is honored
/// only for directories the user has explicitly trusted; trust is remembered per absolute path in
/// <c>$XDG_CONFIG_HOME/freeagent/trusted.json</c>. Modeled on editor workspace-trust: opening a
/// freshly cloned repo no longer silently executes its checked-in config. Deny rules (which only
/// restrict) always apply, trusted or not — see <see cref="PermissionConfig.ApplyTo"/>.
/// </summary>
public static class ProjectTrust
{
    /// <summary>Path to the trust store (a JSON array of absolute directory paths). XDG-aware.</summary>
    public static string StorePath()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(configHome, "freeagent", "trusted.json");
    }

    /// <summary>True if <paramref name="workingDirectory"/> has been trusted.</summary>
    public static bool IsTrusted(string workingDirectory, string? storePath = null) =>
        Load(storePath ?? StorePath()).Contains(Normalize(workingDirectory));

    /// <summary>Remembers <paramref name="workingDirectory"/> as trusted (idempotent).</summary>
    public static void Trust(string workingDirectory, string? storePath = null)
    {
        var path = storePath ?? StorePath();
        var dirs = Load(path);
        if (dirs.Add(Normalize(workingDirectory)))
            Save(path, dirs);
    }

    /// <summary>
    /// Human-readable list of the trust-requiring requests in <paramref name="config"/>. An empty
    /// list means the config has nothing privileged (e.g. only deny rules), so no prompt is needed.
    /// </summary>
    public static IReadOnlyList<string> DescribeRequests(PermissionConfig config)
    {
        var items = new List<string>();

        var hooks = config.Hooks;
        var sessionStart = hooks?.SessionStart?.Count ?? 0;
        var hookCount = sessionStart + (hooks?.PreToolUse?.Count ?? 0) + (hooks?.PostToolUse?.Count ?? 0);
        if (hookCount > 0)
            items.Add($"run {hookCount} shell hook command(s)" + (sessionStart > 0 ? $" ({sessionStart} on session start)" : ""));

        if (config.Mcp?.Servers is { Count: > 0 } mcp)
            items.Add($"launch {mcp.Count} MCP server process(es): {string.Join(", ", mcp.Select(s => s.Name))}");

        if (config.Lsp?.Servers is { Count: > 0 } lsp)
            items.Add($"launch {lsp.Count} LSP server process(es): {string.Join(", ", lsp.Select(s => s.Name))}");

        var grants = (config.AllowTools?.Count ?? 0) + (config.Allow?.Count ?? 0);
        if (grants > 0)
            items.Add($"apply {grants} permission allow-rule(s)");

        return items;
    }

    private static string Normalize(string dir) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

    private static HashSet<string> Load(string path)
    {
        // Linux paths are case-sensitive (Linux-native-first, ADR 0003).
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) is { } dirs)
            {
                foreach (var dir in dirs) set.Add(dir);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt/unreadable trust store fails closed: treat as "nothing trusted".
        }
        return set;
    }

    private static void Save(string path, HashSet<string> dirs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(dirs.OrderBy(d => d, StringComparer.Ordinal)));
    }
}
