using System.Text.Json;
using FreeAgent.Kernel;

namespace FreeAgent.Host;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var workingDir = Environment.CurrentDirectory;
        var baseUrl = GetEnv("OPENAI_BASE_URL", "https://api.openai.com/v1");
        var apiKey = GetEnv("OPENAI_API_KEY", "");
        var model = GetEnv("FREEMODEL", "gpt-4o-mini");
        var options = HostOptions.Parse(args);

        // ── bootstrap ──────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("Error: OPENAI_API_KEY is not set.");
            Console.Error.WriteLine("Set it via environment variable or export it before running.");
            Environment.Exit(1);
            return;
        }

        var provider = new OpenAIProvider(baseUrl, apiKey, model);
        var registry = new ToolRegistry();
        var permissions = new PermissionEngine();
        LoadPermissionConfig(permissions, workingDir);
        var pipeline = new ToolPipeline(registry, permissions);
        var fs = new LinuxAtomicFileSystem();
        var store = new JsonlSessionStore(fs);
        var events = new ConsoleEventSink(options.Verbose);

        // Register real tool adapters
        registry.Register(new ReadFileTool());
        registry.Register(new WriteFileTool());
        registry.Register(new ProcessExecTool());
        registry.Register(new GlobTool());
        registry.Register(new GrepTool());
        registry.Register(new EnterPlanModeTool());
        registry.Register(new ExitPlanModeTool());

        // ── session state ────────────────────────────────────────
        var state = options.Resume
            ? await ResumeOrFreshAsync(store, workingDir, options.ResumeId)
            : NewSession(workingDir);
        var runtime = new SessionRuntime(provider, registry, pipeline, store, events, state);

        // ── launch ─────────────────────────────────────────────────
        PrintBanner();
        Console.WriteLine($"Session: {state.SessionId} | Model: {model} | Working directory: {workingDir}");
        Console.WriteLine("Type 'exit' or press Ctrl+C to quit.\n");

        // Register the Ctrl+C handler once. While a turn is running it cancels that
        // turn (without killing the process); when idle it lets the default abort
        // through so an empty prompt can still be escaped.
        CancellationTokenSource? turnCts = null;
        Console.CancelKeyPress += (_, e) =>
        {
            // Runs on the console signal thread, racing the per-turn cleanup below. Read the field
            // atomically and tolerate a source the loop has already disposed.
            var active = Volatile.Read(ref turnCts);
            if (active is not null)
            {
                e.Cancel = true;
                try { active.Cancel(); }
                catch (ObjectDisposedException) { /* turn already finished; nothing to cancel */ }
            }
        };

        while (true)
        {
            Console.Write("> ");
            string? input = await Console.In.ReadLineAsync();
            if (input is null or "exit" or "quit")
                break;
            if (string.IsNullOrWhiteSpace(input))
                continue;

            if (input.StartsWith('/'))
            {
                HandleCommand(input, state);
                continue;
            }

            turnCts = new CancellationTokenSource();
            try
            {
                Console.WriteLine();
                var result = await runtime.RunTurnAsync(input, turnCts.Token);
                Console.WriteLine();

                if (result.DoomLoopDetected)
                    Console.WriteLine("[Doom loop detected — a repeated tool-call batch was suppressed]");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("\n[Turn cancelled]");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[Error: {ex.Message}]");
            }
            finally
            {
                // Clear the field before disposing so the Ctrl+C handler can never observe a
                // non-null reference to an already-disposed source.
                Interlocked.Exchange(ref turnCts, null)?.Dispose();
            }
        }

        await store.SaveAsync(state, default);
        Console.WriteLine("\nSession saved. Goodbye.");
    }

    private static string GetEnv(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { } value ? value : fallback;

    private static SessionState NewSession(string workingDir) =>
        new(Guid.NewGuid().ToString()[..8], workingDir, DateTimeOffset.UtcNow);

    /// <summary>
    /// Resumes the session persisted at <c>{workingDir}/session.jsonl</c>, optionally requiring its id
    /// to equal <paramref name="requiredId"/>. Any problem (missing file, id mismatch, malformed
    /// transcript) falls back to a fresh session with a clear message — resume never aborts startup.
    /// </summary>
    private static async Task<SessionState> ResumeOrFreshAsync(JsonlSessionStore store, string workingDir, string? requiredId)
    {
        var path = Path.Combine(workingDir, "session.jsonl");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"No session to resume at {path}; starting a new session.");
            return NewSession(workingDir);
        }

        try
        {
            var state = await store.DeserializeAsync(await File.ReadAllTextAsync(path), default);
            if (requiredId is not null && !string.Equals(state.SessionId, requiredId, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"Session in {path} is '{state.SessionId}', not '{requiredId}'; starting a new session.");
                return NewSession(workingDir);
            }

            Console.WriteLine($"Resumed session {state.SessionId} ({state.Messages.Count} message(s) restored).");
            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not resume {path}: {ex.Message}; starting a new session.");
            return NewSession(workingDir);
        }
    }

    /// <summary>
    /// Loads permission allow/deny rules from <c>$FREEAGENT_CONFIG</c> (or <c>.freeagent/config.json</c>
    /// in the working directory) and applies them to the engine. A missing file is fine; a malformed
    /// one is a non-fatal warning so a bad config never blocks startup.
    /// </summary>
    private static void LoadPermissionConfig(PermissionEngine permissions, string workingDir)
    {
        var path = Environment.GetEnvironmentVariable("FREEAGENT_CONFIG");
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(workingDir, ".freeagent", "config.json");

        if (!File.Exists(path))
            return;

        try
        {
            var config = PermissionConfig.Parse(File.ReadAllText(path));
            config.ApplyTo(permissions);
            Console.WriteLine($"Loaded {config.RuleCount} permission rule(s) from {path}");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning: ignoring permission config '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// Handles a slash command typed at the prompt. Today only <c>/plan [on|off]</c> exists (the
    /// model can also toggle it via the EnterPlanMode/ExitPlanMode tools); the switch is the seam for
    /// future commands.
    /// </summary>
    private static void HandleCommand(string input, SessionState state)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "/plan":
                state.PlanMode = parts.Length > 1 && parts[1] is "on" or "off"
                    ? parts[1] == "on"
                    : !state.PlanMode;
                Console.WriteLine(state.PlanMode
                    ? "Plan mode: ON — only read-only tools will run until you turn it off."
                    : "Plan mode: OFF — writable tools are enabled.");
                break;
            default:
                Console.WriteLine($"Unknown command: {parts[0]}. Available: /plan [on|off]");
                break;
        }
    }

    private static void PrintBanner()
    {
        Console.WriteLine(@"
  ███████╗██████╗ ███████╗███████╗ █████╗  ██████╗ ███████╗███╗   ██╗████████╗
  ██╔════╝██╔══██╗██╔════╝██╔════╝██╔══██╗██╔════╝ ██╔════╝████╗  ██║╚══██╔══╝
  █████╗  ██████╔╝█████╗  █████╗  ███████║██║  ███╗█████╗  ██╔██╗ ██║   ██║   
  ██╔══╝  ██╔══██╗██╔══╝  ██╔══╝  ██╔══██║██║   ██║██╔══╝  ██║╚██╗██║   ██║   
  ██║     ██║  ██║███████╗███████╗██║  ██║╚██████╔╝███████╗██║ ╚████║   ██║   
  ╚═╝     ╚═╝  ╚═╝╚══════╝╚══════╝╚═╝  ╚═╝ ╚═════╝ ╚══════╝╚═╝  ╚═══╝   ╚═╝   
");
    }
}
