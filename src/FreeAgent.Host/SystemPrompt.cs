namespace FreeAgent.Host;

/// <summary>
/// Builds the system prompt prepended to a new session. Layers, in order:
/// <list type="number">
///   <item>Base text — either the built-in <see cref="Default"/>, or a user-editable override from a
///         project <c>.freeagent/system.md</c> (precedence) or user
///         <c>$XDG_CONFIG_HOME/freeagent/system.md</c>.</item>
///   <item>Working directory.</item>
///   <item>Git branch (best-effort from <c>.git/HEAD</c> — no subprocess, safely silent on failure).</item>
///   <item>Project context file content — first existing of <c>CLAUDE.md</c>, <c>AGENTS.md</c>,
///         <c>FREEAGENT.md</c> in the working directory.</item>
/// </list>
/// </summary>
public static class SystemPrompt
{
    public const string Default =
        """
        You are FreeAgent, an autonomous coding agent running in a user's terminal. You act directly
        on the files in the working directory using the tools provided to you.

        Guidelines:
        - Use your tools to read, search, edit, and run commands rather than describing what you would
          do. Prefer acting over narrating, and keep going until the task is actually done.
        - Tool results are authoritative. If a tool call is denied, that is final for this turn —
          do NOT claim there is an approval dialog or tell the user to "approve in the interface."
          Instead, adapt, or briefly say which permission is needed and that it can be granted with an
          allow rule in .freeagent/config.json.
        - Be concise. Lead with the result; skip preamble and filler.
        - Make minimal, correct changes and verify them (re-read a file, run a build or test) when practical.
        """;

    /// <summary>Project context files looked for at the working-directory root, in priority order.</summary>
    public static readonly string[] ProjectContextFiles = ["CLAUDE.md", "AGENTS.md", "FREEAGENT.md"];

    /// <summary>The full system-prompt text for a session rooted at <paramref name="workingDirectory"/>.</summary>
    public static string Compose(string workingDirectory)
    {
        var baseText = ReadOverride(workingDirectory) ?? Default;
        var sections = new List<string>
        {
            baseText,
            $"Working directory: {workingDirectory}",
        };

        if (TryReadGitBranch(workingDirectory) is { } branch)
            sections.Add($"Git branch: {branch}");

        if (TryReadProjectContext(workingDirectory) is { } project)
            sections.Add($"--- Project context ({project.Name}) ---\n{project.Content}");

        return string.Join("\n\n", sections);
    }

    private static string? ReadOverride(string workingDirectory)
    {
        foreach (var path in new[] { ProjectPath(workingDirectory), UserPath() })
        {
            try
            {
                if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } text)
                    return text;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ignore an unreadable override and fall through to the next / the default.
            }
        }

        return null;
    }

    /// <summary>Reads the current branch from <c>.git/HEAD</c> (no subprocess); null on any failure or non-repo.</summary>
    public static string? TryReadGitBranch(string workingDirectory)
    {
        var headPath = Path.Combine(workingDirectory, ".git", "HEAD");
        if (!File.Exists(headPath)) return null;

        try
        {
            var head = File.ReadAllText(headPath).Trim();
            const string refPrefix = "ref: refs/heads/";
            if (head.StartsWith(refPrefix, StringComparison.Ordinal))
                return head[refPrefix.Length..];
            // Detached HEAD: short the SHA.
            return $"detached @ {head[..Math.Min(7, head.Length)]}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public sealed record ProjectContext(string Name, string Content);

    /// <summary>First existing project context file (<see cref="ProjectContextFiles"/>) in <paramref name="workingDirectory"/>, or null.</summary>
    public static ProjectContext? TryReadProjectContext(string workingDirectory)
    {
        foreach (var name in ProjectContextFiles)
        {
            var path = Path.Combine(workingDirectory, name);
            try
            {
                if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } content)
                    return new ProjectContext(name, content);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip unreadable file.
            }
        }
        return null;
    }

    private static string ProjectPath(string workingDirectory) =>
        Path.Combine(workingDirectory, ".freeagent", "system.md");

    private static string UserPath()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(configHome, "freeagent", "system.md");
    }
}
