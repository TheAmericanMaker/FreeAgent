using FreeAgent.Kernel;

namespace FreeAgent.Host;

/// <summary>
/// Slash-command handling for the REPL, kept out of <see cref="Program"/> so the text builders and
/// the plan toggle are unit-testable. Input starting with <c>/</c> is dispatched here and never sent
/// to the model. (The eventual TUI replaces these with a command palette — see ADR 0005 / ROADMAP.)
/// </summary>
public static class HostCommands
{
    public static void Handle(string input, SessionState state, string model)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "/help":
                Console.WriteLine(HelpText());
                break;
            case "/status":
                Console.WriteLine(StatusText(state, model));
                break;
            case "/model":
                Console.WriteLine(ModelText(model));
                break;
            case "/plan":
                Console.WriteLine(ApplyPlan(state, parts));
                break;
            default:
                Console.WriteLine($"Unknown command: {parts[0]}. Try /help.");
                break;
        }
    }

    public static string HelpText() =>
        """
        Commands:
          /help            Show this help.
          /status          Session id, model, working directory, message count, plan mode.
          /model           Show the active model and how to change it.
          /plan [on|off]   Toggle plan mode (only read-only tools run).
          exit | quit      End the session (also Ctrl+D / EOF).
          Ctrl+C           Cancel the current turn without quitting.
        """;

    public static string StatusText(SessionState state, string model) =>
        $"""
        Session:    {state.SessionId}
        Model:      {model}
        Directory:  {state.WorkingDirectory}
        Messages:   {state.Messages.Count}
        Plan mode:  {(state.PlanMode ? "ON (read-only tools only)" : "off")}
        Approvals:  {(state.SessionApprovals.Count == 0 ? "none granted this session" : string.Join(", ", state.SessionApprovals))}
        """;

    public static string ModelText(string model) =>
        $"Model: {model}\nChange it with the FREEMODEL env var or \"model\" in ~/.config/freeagent/config.json (restart to apply).";

    /// <summary>Applies <c>/plan</c> (toggle, or <c>on</c>/<c>off</c>), mutating the session and returning the status line.</summary>
    public static string ApplyPlan(SessionState state, string[] parts)
    {
        state.PlanMode = parts.Length > 1 && parts[1] is "on" or "off"
            ? parts[1] == "on"
            : !state.PlanMode;

        return state.PlanMode
            ? "Plan mode: ON — only read-only tools will run until you turn it off."
            : "Plan mode: OFF — writable tools are enabled.";
    }
}
