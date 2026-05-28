namespace FreeAgent.Host;

/// <summary>
/// Builds the system prompt prepended to a new session. There's a built-in default that grounds the
/// model (identity, how to use tools, how the host behaves), overridable by a user-editable file:
/// a project-level <c>.freeagent/system.md</c> takes precedence, then
/// <c>$XDG_CONFIG_HOME/freeagent/system.md</c> (default <c>~/.config/freeagent/system.md</c>). The
/// resolved base is always followed by a runtime context line (the working directory) so grounding
/// stays correct even with a custom prompt.
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

    /// <summary>The full system-prompt text for a session rooted at <paramref name="workingDirectory"/>.</summary>
    public static string Compose(string workingDirectory)
    {
        var baseText = ReadOverride(workingDirectory) ?? Default;
        return $"{baseText}\n\nWorking directory: {workingDirectory}";
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
