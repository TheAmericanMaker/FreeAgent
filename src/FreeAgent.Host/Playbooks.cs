namespace FreeAgent.Host;

/// <summary>
/// User-defined prompt shortcuts ("playbooks"): a directory of Markdown files whose contents are
/// the prompt the user wants to send. Invoked via the host's <c>/run &lt;name&gt; [args]</c>
/// command; <c>{{arg1}}…{{argN}}</c> placeholders are substituted positionally so the same playbook
/// can take parameters. Loaded from
/// <c>./.freeagent/playbooks/</c> (project) and <c>$XDG_CONFIG_HOME/freeagent/playbooks/</c> (user);
/// project takes precedence on a name collision. Pure (file I/O lives in <see cref="LoadAll"/>).
/// </summary>
public static class Playbooks
{
    /// <summary>Discovers playbooks in the project + user directories. Returns name → template.</summary>
    public static IReadOnlyDictionary<string, string> LoadAll(string workingDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        // Load user-level first, then project so project overrides on collision.
        foreach (var dir in new[] { UserDir(), ProjectDir(workingDirectory) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
            {
                try
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var content = File.ReadAllText(file).Trim();
                    if (content.Length > 0)
                        result[name] = content;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* skip */ }
            }
        }

        return result;
    }

    /// <summary>
    /// Renders <paramref name="name"/> with positional substitutions <c>{{arg1}}…{{argN}}</c>.
    /// Returns null if the playbook isn't found. Pure.
    /// </summary>
    public static string? Render(IReadOnlyDictionary<string, string> playbooks, string name, IReadOnlyList<string> args)
    {
        if (!playbooks.TryGetValue(name, out var template))
            return null;
        var rendered = template;
        for (var i = 0; i < args.Count; i++)
            rendered = rendered.Replace($"{{{{arg{i + 1}}}}}", args[i], StringComparison.Ordinal);
        return rendered;
    }

    private static string ProjectDir(string workingDirectory) =>
        Path.Combine(workingDirectory, ".freeagent", "playbooks");

    private static string UserDir()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(configHome, "freeagent", "playbooks");
    }
}
