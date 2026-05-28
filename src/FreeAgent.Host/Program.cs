using System.Reflection;
using System.Text.Json;
using FreeAgent.Kernel;

namespace FreeAgent.Host;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var options = HostOptions.Parse(args);
        if (options.Help)
        {
            PrintHelp();
            return;
        }
        if (options.Version)
        {
            Console.WriteLine($"freeagent {Version}");
            return;
        }

        var workingDir = Environment.CurrentDirectory;
        var providerConfig = ProviderConfig.Load();
        var providerName = providerConfig.ResolveProvider();
        var settings = providerConfig.SettingsFor(providerName);
        var baseUrl = settings.BaseUrl!;
        var apiKey = settings.ApiKey!;
        var model = settings.Model!;

        // ── bootstrap ──────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var keyEnv = providerName == "anthropic" ? "ANTHROPIC_API_KEY" : "OPENAI_API_KEY";
            Console.Error.WriteLine($"Error: no API key found for provider '{providerName}'.");
            Console.Error.WriteLine($"Set {keyEnv}, or add the key to {ProviderConfig.ConfigPath()}.");
            Environment.Exit(1);
            return;
        }

        IProvider provider = providerName switch
        {
            "anthropic" => new AnthropicProvider(baseUrl, apiKey, model),
            _ => new OpenAIProvider(baseUrl, apiKey, model),
        };
        var registry = new ToolRegistry();
        var permissions = new PermissionEngine();
        LoadPermissionConfig(permissions, workingDir);
        var pipeline = new ToolPipeline(registry, permissions, new ConsoleApprover(workingDir));
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
        if (int.TryParse(Environment.GetEnvironmentVariable("FREE_CONTEXT_TOKENS"), out var ctx) && ctx > 0)
            state.ContextWindow = ctx;
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
                HostCommands.Handle(input, state, model);
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

    private static string Version
    {
        get
        {
            var informational = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            // Strip any "+<git-sha>" build metadata SourceLink may append.
            var trimmed = informational?.Split('+', 2)[0];
            return trimmed ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            freeagent {Version} — interactive agent CLI over any OpenAI-compatible endpoint.

            USAGE
              freeagent [options]

            Run it from the directory you want the agent to work in; that directory is its sandbox.

            OPTIONS
              -h, --help        Show this help and exit.
                  --version     Show the version and exit.
              -v, --verbose     Stream the model's reasoning (dimmed) and per-turn token usage.
                  --resume [id] Resume the session in ./session.jsonl (optionally requiring its id).

            PROMPT COMMANDS
              /plan [on|off]    Toggle plan mode (only read-only tools run).
              exit | quit       End the session (also saved on Ctrl+D / EOF).
              Ctrl+C            Cancel the current turn without quitting.

            CONFIGURATION (precedence: provider-specific env > config section > legacy > default)
              FREEPROVIDER       Active provider — "openai" (default) or "anthropic".
              OPENAI_API_KEY     Key for the OpenAI-compatible provider.
              OPENAI_BASE_URL    Endpoint base (default {ProviderConfig.DefaultBaseUrl}).
              ANTHROPIC_API_KEY  Key for the native Anthropic provider.
              ANTHROPIC_BASE_URL Endpoint base (default {ProviderConfig.AnthropicDefaultBaseUrl}).
              FREEMODEL          Model name (provider-agnostic env override).
              User config:       {ProviderConfig.ConfigPath()}  (provider + per-provider sections)
              Permissions:       ./.freeagent/config.json  (allow/deny rules)
            """);
    }

    private static SessionState NewSession(string workingDir)
    {
        var state = new SessionState(Guid.NewGuid().ToString()[..8], workingDir, DateTimeOffset.UtcNow);
        state.Messages.Add(new Message(MessageRole.System, SystemPrompt.Compose(workingDir)));
        return state;
    }

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
