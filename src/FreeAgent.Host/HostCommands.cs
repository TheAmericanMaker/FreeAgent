using FreeAgent.Kernel;

namespace FreeAgent.Host;

/// <summary>
/// Slash-command handling for the REPL, kept out of <see cref="Program"/> so the text builders and
/// the plan toggle are unit-testable. Input starting with <c>/</c> is dispatched here and never sent
/// to the model. (The eventual TUI replaces these with a command palette — see ADR 0005 / ROADMAP.)
/// </summary>
public static class HostCommands
{
    /// <summary>Diagnostic context the host snapshots once for <c>/doctor</c>.</summary>
    public sealed record Diagnostics(
        string ProviderName, string Model, string BaseUrl, string ConfigPath,
        IReadOnlyList<string> ToolNames, IReadOnlyList<string> AgentTypes);

    public static void Handle(string input, SessionState state, string model, Diagnostics diagnostics)
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
            case "/undo":
                Console.WriteLine(Undo(state));
                break;
            case "/revert":
                Console.WriteLine(Revert(state, parts));
                break;
            case "/tag":
                Console.WriteLine(Tag(state, parts));
                break;
            case "/untag":
                Console.WriteLine(Untag(state, parts));
                break;
            case "/doctor":
                Console.WriteLine(DoctorText(state, diagnostics));
                break;
            case "/serve":
                Console.WriteLine(Serve(parts).GetAwaiter().GetResult());
                break;
            case "/fork":
                Console.WriteLine(Fork(state).GetAwaiter().GetResult());
                break;
            case "/commands":
                Console.WriteLine(CommandsList(parts));
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
          /undo            Revert the most recent file change this session.
          /revert [N]      Drop the last N user turns from the transcript (default 1). Files are not reverted (use /undo).
          /tag <name>      Add a session tag (visible in /status and /doctor).
          /untag <name>    Remove a session tag.
          /run <name> ...  Run a playbook (.md files in .freeagent/playbooks / ~/.config/freeagent/playbooks).
                           Positional args become {{arg1}}, {{arg2}}, … Bare `/run` lists available.
          /doctor          Print a one-shot configuration + health snapshot.
          /serve start <model-path-or-name> [--port N] [--bin <path>] [-- <extra args>]
                           Spawn a local OpenAI-compat inference server (llama-server by default).
                           Prints the OPENAI_BASE_URL line to point FreeAgent at it.
                           A bare name is resolved against the downloaded-model catalog.
          /serve stop      Kill the running local server (if any).
          /serve status    Show whether the local server is running.
          /serve download <url-or-hf:owner/repo/path.gguf> [--name <local-name>]
                           Stream a GGUF into the local catalog. HF_TOKEN is forwarded for gated
                           HuggingFace repositories.
          /serve models    List GGUFs currently in the local catalog.
          /fork            Snapshot the current session to session-fork-<id>.jsonl so you can
                           branch the conversation. Resume later with `mv …jsonl session.jsonl
                           && freeagent --resume <id>`.
          /commands [q]    List registered host commands (fuzzy filter by [q]). Same registry the
                           future TUI/editor frontends bind their command palette to.
          exit | quit      End the session (also Ctrl+D / EOF).
          Ctrl+C           Cancel the current turn without quitting.
        """;

    public static string StatusText(SessionState state, string model) =>
        $"""
        Session:    {state.SessionId}
        Model:      {model}
        Directory:  {state.WorkingDirectory}
        Messages:   {state.Messages.Count}
        Iterations: {state.TotalIterations}{(state.SessionIterationLimit is { } cap ? $" / {cap}" : "")}
        Plan mode:  {(state.PlanMode ? "ON (read-only tools only)" : "off")}
        Tags:       {(state.Tags.Count == 0 ? "none" : string.Join(", ", state.Tags))}
        Approvals:  {(state.SessionApprovals.Count == 0 ? "none granted this session" : string.Join(", ", state.SessionApprovals))}
        """;

    public static string ModelText(string model) =>
        $"Model: {model}\nChange it with the FREEMODEL env var or \"model\" in ~/.config/freeagent/config.json (restart to apply).";

    /// <summary>One-shot configuration + tool inventory snapshot. No network probes (kept offline-safe).</summary>
    public static string DoctorText(SessionState state, Diagnostics d) =>
        $"""
        FreeAgent diagnostic
          Provider:   {d.ProviderName}
          Model:      {d.Model}
          Base URL:   {d.BaseUrl}
          Workdir:    {state.WorkingDirectory}
          User config: {d.ConfigPath}
          Plan mode:  {(state.PlanMode ? "ON" : "off")}
          Tools ({d.ToolNames.Count}): {string.Join(", ", d.ToolNames)}
          Sub-agents ({d.AgentTypes.Count}): {(d.AgentTypes.Count == 0 ? "none" : string.Join(", ", d.AgentTypes))}
          Session approvals: {(state.SessionApprovals.Count == 0 ? "none" : string.Join(", ", state.SessionApprovals))}
          File undo stack: {state.History.Count} entr{(state.History.Count == 1 ? "y" : "ies")}
        """;

    /// <summary>
    /// Pops the most recent <see cref="FileSnapshot"/> and restores the file: writes back the
    /// previous content, or deletes the file if it didn't exist before the change. Returns a
    /// status line. Done at host level so it bypasses the permission engine (it's the user's
    /// explicit revert, not a model-driven write).
    /// </summary>
    public static string Undo(SessionState state)
    {
        if (!state.History.TryPop(out var snapshot))
            return "Nothing to undo.";

        try
        {
            if (snapshot.PreviousContent is null)
            {
                if (File.Exists(snapshot.Path))
                    File.Delete(snapshot.Path);
                return $"Undone: deleted {snapshot.Path} (it did not exist before the change).";
            }

            // Capture the current content so we can show what the undo reverts.
            string? currentContent = null;
            try { currentContent = File.ReadAllText(snapshot.Path); }
            catch { /* fall back to a no-diff message */ }

            File.WriteAllText(snapshot.Path, snapshot.PreviousContent);

            var summary = $"Undone: restored {snapshot.Path} to its previous contents.";
            if (currentContent is not null && currentContent != snapshot.PreviousContent)
            {
                // Show what just got reverted: the diff from "current" → "previous" before the
                // undo, i.e. what was rolled back. Color is on by default for the host console.
                var diff = ColoredDiff.Render(
                    currentContent,
                    snapshot.PreviousContent,
                    oldLabel: $"{snapshot.Path} (reverted)",
                    newLabel: $"{snapshot.Path} (restored)",
                    color: true);
                if (diff.Length > 0)
                    summary += "\n" + diff;
            }
            return summary;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Undo failed for {snapshot.Path}: {ex.Message}";
        }
    }

    /// <summary>
    /// Drops the most recent <paramref name="parts"/>[1] user turns from <paramref name="state"/>'s
    /// transcript (default 1). Useful when the model went down a bad path: revert + retype. Does not
    /// touch files — use <c>/undo</c> for that. Leading System messages are preserved.
    /// </summary>
    public static string Revert(SessionState state, string[] parts)
    {
        var n = 1;
        if (parts.Length > 1 && int.TryParse(parts[1], out var parsed))
            n = parsed;
        if (n < 1)
            return "Revert needs a positive number of turns.";

        var userIndices = new List<int>();
        for (var i = 0; i < state.Messages.Count; i++)
            if (state.Messages[i].Role == MessageRole.User)
                userIndices.Add(i);

        if (userIndices.Count == 0)
            return "Nothing to revert (no user turns yet).";

        var targetTurn = userIndices.Count - n;
        if (targetTurn < 0)
            return $"Only {userIndices.Count} user turn(s) in this session; cannot revert {n}.";

        var truncateAt = userIndices[targetTurn];
        var dropped = state.Messages.Count - truncateAt;
        while (state.Messages.Count > truncateAt)
            state.Messages.RemoveAt(state.Messages.Count - 1);

        return $"Reverted {n} turn(s); dropped {dropped} message(s). Files are unchanged — use /undo to roll back writes.";
    }

    /// <summary>Adds <paramref name="parts"/>[1] to <paramref name="state"/>'s tags.</summary>
    public static string Tag(SessionState state, string[] parts)
    {
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
            return "Usage: /tag <name>";
        return state.Tags.Add(parts[1])
            ? $"Tagged: {parts[1]}"
            : $"Already tagged: {parts[1]}";
    }

    /// <summary>Removes <paramref name="parts"/>[1] from <paramref name="state"/>'s tags.</summary>
    public static string Untag(SessionState state, string[] parts)
    {
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
            return "Usage: /untag <name>";
        return state.Tags.Remove(parts[1])
            ? $"Untagged: {parts[1]}"
            : $"No such tag: {parts[1]}";
    }

    /// <summary>
    /// Parses the arguments for <c>/serve start</c> into a structured shape. Pure so tests can
    /// exercise every flag combination without spawning a subprocess.
    /// </summary>
    public sealed record ServeStartArgs(string ModelPath, int Port, string BinPath, string ExtraArgs);

    /// <summary>
    /// Parses <c>/serve start &lt;path&gt; [--port N] [--bin path] [-- extra…]</c>. Returns null with
    /// an error message on bad input.
    /// </summary>
    public static (ServeStartArgs? Args, string? Error) ParseServeStart(string[] parts)
    {
        if (parts.Length < 3)
            return (null, "Usage: /serve start <model-path> [--port N] [--bin <path>] [-- <extra args>]");

        string? model = null;
        var port = 8080;
        var bin = "llama-server";
        var extra = new List<string>();
        var afterDoubleDash = false;

        for (var i = 2; i < parts.Length; i++)
        {
            var tok = parts[i];
            if (afterDoubleDash) { extra.Add(tok); continue; }
            switch (tok)
            {
                case "--":
                    afterDoubleDash = true;
                    break;
                case "--port" when i + 1 < parts.Length && int.TryParse(parts[i + 1], out var p) && p is > 0 and < 65536:
                    port = p; i++;
                    break;
                case "--port":
                    return (null, "--port needs a valid TCP port (1–65535).");
                case "--bin" when i + 1 < parts.Length:
                    bin = parts[i + 1]; i++;
                    break;
                case "--bin":
                    return (null, "--bin needs a path.");
                default:
                    if (tok.StartsWith("--"))
                        return (null, $"Unknown flag: {tok}");
                    if (model is not null)
                        return (null, $"Unexpected argument '{tok}' — model path was already '{model}'. Use '--' before extra server args.");
                    model = tok;
                    break;
            }
        }

        if (model is null)
            return (null, "/serve start needs a <model-path>.");

        return (new ServeStartArgs(model, port, bin, string.Join(' ', extra)), null);
    }

    /// <summary>Parsed <c>/serve download</c> arguments. Pure for testability.</summary>
    public sealed record ServeDownloadArgs(string Source, string? OverrideName);

    /// <summary>
    /// Parses <c>/serve download &lt;url-or-hf-spec&gt; [--name &lt;local-name&gt;]</c>. The source token
    /// is required and positional; <c>--name</c> overrides the inferred filename.
    /// </summary>
    public static (ServeDownloadArgs? Args, string? Error) ParseServeDownload(string[] parts)
    {
        if (parts.Length < 3)
            return (null, "Usage: /serve download <url-or-hf-spec> [--name <local-name>]");

        string? source = null;
        string? overrideName = null;

        for (var i = 2; i < parts.Length; i++)
        {
            var tok = parts[i];
            switch (tok)
            {
                case "--name" when i + 1 < parts.Length:
                    overrideName = parts[i + 1]; i++;
                    break;
                case "--name":
                    return (null, "--name needs a filename.");
                default:
                    if (tok.StartsWith("--"))
                        return (null, $"Unknown flag: {tok}");
                    if (source is not null)
                        return (null, $"Unexpected argument '{tok}' — source was already '{source}'.");
                    source = tok;
                    break;
            }
        }

        if (source is null)
            return (null, "/serve download needs a URL or hf:owner/repo/path/to/file.gguf spec.");
        return (new ServeDownloadArgs(source, overrideName), null);
    }

    /// <summary>Dispatch for <c>/serve {start|stop|status|download|models}</c>. Awaits the launcher.</summary>
    public static async Task<string> Serve(string[] parts)
    {
        if (parts.Length < 2)
            return "Usage: /serve {start|stop|status|download|models} …  (see /help)";

        switch (parts[1].ToLowerInvariant())
        {
            case "start":
                {
                    var (args, err) = ParseServeStart(parts);
                    if (args is null) return err ?? "Bad /serve start arguments.";
                    // Allow short catalog names ("qwen2.5-coder.gguf") in addition to absolute
                    // paths. ResolveModelName returns the input unchanged if nothing matches, so
                    // the launcher's missing-file error still surfaces.
                    var resolved = ModelServerLauncher.ResolveModelName(args.ModelPath);
                    return await ModelServerLauncher.StartAsync(
                        resolved, args.Port, args.BinPath, args.ExtraArgs, CancellationToken.None);
                }
            case "stop":
                return ModelServerLauncher.Stop();
            case "status":
                return ModelServerLauncher.Status();
            case "download":
                {
                    var (args, err) = ParseServeDownload(parts);
                    if (args is null) return err ?? "Bad /serve download arguments.";
                    return await ModelServerLauncher.DownloadAsync(args.Source, args.OverrideName, CancellationToken.None);
                }
            case "models":
                return ModelServerLauncher.ListCatalog();
            default:
                return $"Unknown /serve subcommand '{parts[1]}'. Use start, stop, status, download, or models.";
        }
    }

    /// <summary>
    /// Single source of truth for the host's command palette. Every <c>/foo</c> slash command the
    /// dispatcher above understands is registered here with category + label + description so
    /// future frontends (TUI, VS Code) can drive the same palette without re-listing commands.
    /// </summary>
    public static CommandRegistry BuildDefaultRegistry()
    {
        var registry = new CommandRegistry();
        registry.Register(new("help", "Help", "List the commands the host understands.", Shortcut: "/help", Category: "Session"));
        registry.Register(new("status", "Show session status", "Session id, model, working dir, message count, plan mode.", Shortcut: "/status", Category: "Session"));
        registry.Register(new("model", "Show active model", "How to change provider and model.", Shortcut: "/model", Category: "Session"));
        registry.Register(new("plan.toggle", "Toggle plan mode", "Only read-only tools run while plan mode is on.", Shortcut: "/plan", Category: "Plan"));
        registry.Register(new("undo", "Undo last file change", "Revert the most recent agent-driven file write.", Shortcut: "/undo", Category: "Editing"));
        registry.Register(new("revert", "Revert N user turns", "Drop the last N user turns from the transcript (files unchanged).", Shortcut: "/revert", Category: "Editing"));
        registry.Register(new("tag", "Tag session", "Add a tag visible in /status and /doctor.", Shortcut: "/tag", Category: "Session"));
        registry.Register(new("untag", "Untag session", "Remove a session tag.", Shortcut: "/untag", Category: "Session"));
        registry.Register(new("doctor", "Diagnostics snapshot", "Provider, model, tool inventory, sub-agent roles, plan mode, approvals.", Shortcut: "/doctor", Category: "Diagnostics"));
        registry.Register(new("session.fork", "Fork session", "Snapshot the current transcript to a sibling JSONL so you can branch the conversation.", Shortcut: "/fork", Category: "Session"));
        registry.Register(new("serve.start", "Start local model server", "Launch llama-server (or any OpenAI-compat binary) and print the OPENAI_BASE_URL line.", Shortcut: "/serve start …", Category: "Local model"));
        registry.Register(new("serve.stop", "Stop local model server", "Kill the recorded model-server process.", Shortcut: "/serve stop", Category: "Local model"));
        registry.Register(new("serve.status", "Model server status", "Is the local model server running?", Shortcut: "/serve status", Category: "Local model"));
        registry.Register(new("serve.download", "Download GGUF", "Stream a GGUF into the local catalog (HTTPS URL or hf:owner/repo/path.gguf).", Shortcut: "/serve download …", Category: "Local model"));
        registry.Register(new("serve.models", "List downloaded models", "Show the GGUFs in the local model catalog.", Shortcut: "/serve models", Category: "Local model"));
        registry.Register(new("run", "Run playbook", "Render a Markdown playbook with positional args and dispatch the user turn.", Shortcut: "/run <name> [args]", Category: "Playbooks"));
        registry.Register(new("commands", "Show command palette", "Fuzzy list of every registered host command (the same registry the TUI palette will use).", Shortcut: "/commands [q]", Category: "Diagnostics"));
        return registry;
    }

    /// <summary>Render the command list (optionally filtered) for the REPL.</summary>
    public static string CommandsList(string[] parts)
    {
        var query = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : string.Empty;
        var registry = BuildDefaultRegistry();
        var commands = registry.Search(query);
        if (commands.Count == 0)
            return $"No commands match '{query}'.";

        var width = commands.Max(c => (c.Shortcut ?? c.Id).Length);
        var lines = new List<string>(commands.Count + 1);
        var lastCategory = "";
        foreach (var c in commands)
        {
            var cat = c.Category ?? "Misc";
            if (cat != lastCategory)
            {
                if (lines.Count > 0) lines.Add(string.Empty);
                lines.Add($"[{cat}]");
                lastCategory = cat;
            }
            var key = (c.Shortcut ?? c.Id).PadRight(width);
            lines.Add($"  {key}  {c.Label}{(string.IsNullOrEmpty(c.Description) ? "" : $" — {c.Description}")}");
        }
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Snapshots the current transcript to a forked session file alongside the live one. The clone
    /// gets a fresh 8-character id and is persisted via a separate <see cref="JsonlSessionStore"/>
    /// so the live session is never touched; the user can later promote the fork by moving it back
    /// to <c>session.jsonl</c> and passing the fork id to <c>--resume</c>. Empty sessions are
    /// rejected (nothing to clone).
    /// </summary>
    public static async Task<string> Fork(SessionState state)
    {
        if (state.Messages.Count == 0)
            return "Nothing to fork yet (no messages in the session).";

        var newId = Guid.NewGuid().ToString("N")[..8];
        var forked = new SessionState(newId, state.WorkingDirectory, DateTimeOffset.UtcNow);
        foreach (var m in state.Messages)
            forked.Messages.Add(m);

        var forkPath = Path.Combine(state.WorkingDirectory, $"session-fork-{newId}.jsonl");
        var store = new JsonlSessionStore(path: forkPath);
        try { await store.SaveAsync(forked, CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return $"Fork failed: {ex.Message}";
        }

        return $"Forked: {forked.Messages.Count} message(s) snapshot to {forkPath}.\n"
             + $"Resume later: mv {Path.GetFileName(forkPath)} session.jsonl && freeagent --resume {newId}";
    }

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
