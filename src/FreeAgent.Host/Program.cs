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
            var keyEnv = providerName switch
            {
                "anthropic" => "ANTHROPIC_API_KEY",
                "azure" => "AZURE_OPENAI_API_KEY",
                _ => "OPENAI_API_KEY",
            };
            Console.Error.WriteLine($"Error: no API key found for provider '{providerName}'.");
            Console.Error.WriteLine($"Set {keyEnv}, or add the key to {ProviderConfig.ConfigPath()}.");
            Environment.Exit(1);
            return;
        }

        IProvider provider = providerName switch
        {
            "anthropic" => new AnthropicProvider(baseUrl, apiKey, model),
            "azure" => new AzureOpenAIProvider(
                endpoint: baseUrl, apiKey: apiKey, deployment: model,
                apiVersion: settings.ApiVersion ?? AzureOpenAIProvider.DefaultApiVersion),
            _ => new OpenAIProvider(baseUrl, apiKey, model),
        };
        var registry = new ToolRegistry();
        var permissions = new PermissionEngine();
        var projectConfig = LoadProjectConfig(permissions, workingDir);
        var hookRunner = new HookRunner(projectConfig?.Hooks, new BashShellExecutor());
        var artifactStore = new InMemoryArtifactStore();
        var pipeline = new ToolPipeline(
            registry,
            permissions,
            approver: new ConsoleApprover(workingDir),
            cache: new InMemoryToolResultCache(),
            hooks: hookRunner,
            artifacts: artifactStore);
        var fs = new LinuxAtomicFileSystem();
        var store = new JsonlSessionStore(fs);
        var events = new ConsoleEventSink(options.Verbose);

        // Register real tool adapters
        registry.Register(new ReadFileTool());
        registry.Register(new WriteFileTool());
        registry.Register(new EditFileTool());
        registry.Register(new MultiEditFileTool());
        registry.Register(new ApplyPatchTool());
        registry.Register(new ProcessExecTool());
        registry.Register(new GlobTool());
        registry.Register(new GrepTool());
        registry.Register(new EnterPlanModeTool());
        registry.Register(new ExitPlanModeTool());
        registry.Register(new ReadMemoryTool());
        registry.Register(new WriteMemoryTool());
        registry.Register(new ReadArtifactTool(artifactStore));

        // Sub-agents — restricted-tool roles that the main agent can spawn for sub-tasks.
        var agents = new AgentRegistry();
        agents.Register(new AgentDefinition(
            "Explore",
            ["ReadFile", "Glob", "Grep", "ReadMemory"],
            "You are an Explore sub-agent. You may only read and search the workspace; report what you find concisely."));
        agents.Register(new AgentDefinition(
            "Plan",
            ["ReadFile", "Glob", "Grep", "ReadMemory", "EnterPlanMode", "ExitPlanMode"],
            "You are a Plan sub-agent. Investigate the task and produce a step-by-step plan. Do not make changes."));
        agents.Register(new AgentDefinition(
            "Coder",
            ["ReadFile", "WriteFile", "EditFile", "Glob", "Grep", "ProcessExec", "ReadMemory", "WriteMemory", "EnterPlanMode", "ExitPlanMode"],
            "You are a Coder sub-agent. Implement the task with minimal, correct changes; verify when practical."));
        agents.Register(new AgentDefinition(
            "Verify",
            ["ReadFile", "Glob", "Grep", "ProcessExec", "ReadMemory"],
            "You are a Verify sub-agent. Run tests, lints, and other verifications; report findings concisely."));
        var subAgentRunner = new SubAgentRunner(
            provider, registry, permissions, agents,
            approver: new ConsoleApprover(workingDir));
        registry.Register(new SpawnAgentTool(subAgentRunner, agents));

        // ── session state ────────────────────────────────────────
        var state = options.Resume
            ? await ResumeOrFreshAsync(store, workingDir, options.ResumeId)
            : NewSession(workingDir);
        if (int.TryParse(Environment.GetEnvironmentVariable("FREE_CONTEXT_TOKENS"), out var ctx) && ctx > 0)
            state.ContextWindow = ctx;

        // SessionStart hooks run once per session (after state creation, before the first turn).
        await hookRunner.RunSessionStartAsync(state, default);

        // Snapshot of host configuration for /doctor.
        var diagnostics = new HostCommands.Diagnostics(
            providerName, model, baseUrl, ProviderConfig.ConfigPath(),
            registry.Definitions.Select(d => d.Name).ToList(),
            agents.Types.ToList());
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
                HostCommands.Handle(input, state, model, diagnostics);
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
    /// Loads <c>.freeagent/config.json</c> (or <c>$FREEAGENT_CONFIG</c>): applies permission rules
    /// to the engine and returns the parsed config so the host can also wire its hooks. A missing
    /// file returns null; a malformed one is a non-fatal warning that never blocks startup.
    /// </summary>
    private static PermissionConfig? LoadProjectConfig(PermissionEngine permissions, string workingDir)
    {
        var path = Environment.GetEnvironmentVariable("FREEAGENT_CONFIG");
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(workingDir, ".freeagent", "config.json");

        if (!File.Exists(path))
            return null;

        try
        {
            var config = PermissionConfig.Parse(File.ReadAllText(path));
            config.ApplyTo(permissions);
            Console.WriteLine($"Loaded {config.RuleCount} permission rule(s) from {path}");
            return config;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning: ignoring config '{path}': {ex.Message}");
            return null;
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
