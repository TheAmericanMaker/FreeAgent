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

        // `freeagent setup` — interactive provider-config wizard. Doesn't load providers,
        // doesn't try to contact a network, doesn't insist on an API key being already set.
        if (options.Subcommand == HostSubcommand.Setup)
        {
            await InteractiveSetup.RunAsync();
            return;
        }

        // `freeagent trust` — remember the current directory as trusted so its .freeagent hooks,
        // MCP/LSP servers, and allow-rules run without prompting in future sessions.
        if (options.Subcommand == HostSubcommand.Trust)
        {
            ProjectTrust.Trust(Environment.CurrentDirectory);
            Console.WriteLine($"✓ Trusted {Environment.CurrentDirectory}.");
            Console.WriteLine("  Its .freeagent hooks, MCP/LSP servers, and allow-rules will run from now on.");
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
        // Ollama, Bedrock, and Vertex are unauthenticated-here by default (Bedrock uses the AWS
        // credential chain; Vertex uses GCP ADC, not an inline API key); everyone else requires an
        // API key in the bootstrap.
        if (string.IsNullOrWhiteSpace(apiKey)
            && providerName != "ollama"
            && providerName != "bedrock"
            && providerName != "vertex")
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

        // Per-request budgets (Anthropic-only today). FREE_MAX_TOKENS overrides the default 4096
        // ceiling on the visible reply; FREE_THINKING_BUDGET enables extended thinking with the
        // given token budget for the reasoning trace.
        var anthropicMaxTokens = int.TryParse(Environment.GetEnvironmentVariable("FREE_MAX_TOKENS"), out var mt) && mt > 0
            ? mt : AnthropicProvider.DefaultMaxTokens;
        var anthropicThinkingBudget = int.TryParse(Environment.GetEnvironmentVariable("FREE_THINKING_BUDGET"), out var tb) && tb > 0
            ? tb : 0;

        // Ollama-only tuning. Both knobs are opt-in; Ollama uses the model's Modelfile defaults
        // otherwise.
        var ollamaNumCtx = int.TryParse(Environment.GetEnvironmentVariable("FREE_NUM_CTX"), out var nc) && nc > 0
            ? nc : (int?)null;
        var ollamaTemperature = double.TryParse(Environment.GetEnvironmentVariable("FREE_TEMPERATURE"), System.Globalization.CultureInfo.InvariantCulture, out var tp)
            ? tp : (double?)null;

        IProvider provider = providerName switch
        {
            "anthropic" => new AnthropicProvider(baseUrl, apiKey, model, anthropicMaxTokens, anthropicThinkingBudget),
            "azure" => new AzureOpenAIProvider(
                endpoint: baseUrl, apiKey: apiKey, deployment: model,
                apiVersion: settings.ApiVersion ?? AzureOpenAIProvider.DefaultApiVersion),
            "ollama" => new OllamaProvider(baseUrl, model, ollamaNumCtx, ollamaTemperature),
            // For Bedrock, settings.BaseUrl carries the AWS region (not an HTTP URL); the SDK
            // turns it into an endpoint via RegionEndpoint.GetBySystemName. Auth flows through
            // the default AWS credential chain.
            "bedrock" => new BedrockProvider(region: baseUrl, modelId: model, maxTokens: anthropicMaxTokens),
            // For Vertex, settings.BaseUrl is the GCP project id and ApiVersion carries the
            // region/location. Auth uses Google Application Default Credentials.
            "vertex" => new VertexProvider(
                projectId: baseUrl,
                location: settings.ApiVersion ?? ProviderConfig.VertexDefaultLocation,
                modelId: model,
                maxTokens: anthropicMaxTokens),
            _ => new OpenAIProvider(baseUrl, apiKey, model),
        };
        var registry = new ToolRegistry();
        var permissions = new PermissionEngine();

        // Workspace trust: a project's .freeagent config can run code (hooks, MCP/LSP servers) and
        // grant privileges (allow rules). Honor that content only for a trusted directory; otherwise
        // apply deny-rules only and skip the executable surfaces, so opening a cloned repo can't
        // silently execute its checked-in config. Deny rules always apply.
        var projectConfig = ParseProjectConfig(workingDir);
        var trusted = ResolveTrust(projectConfig, workingDir, options.Trust);
        if (projectConfig is not null)
        {
            projectConfig.ApplyTo(permissions, includeGrants: trusted);
            if (projectConfig.RuleCount > 0)
                Console.WriteLine($"Loaded {projectConfig.RuleCount} permission rule(s) from {ProjectConfigPath(workingDir)}"
                    + (trusted ? "" : " (allow-rules skipped — directory not trusted)"));
        }

        // Hooks only run when the directory is trusted (null config ⇒ all hook seams are no-ops).
        var hookRunner = new HookRunner(trusted ? projectConfig?.Hooks : null, new BashShellExecutor());
        var artifactStore = new InMemoryArtifactStore();
        var pipeline = new ToolPipeline(
            registry,
            permissions,
            approver: new ConsoleApprover(workingDir),
            cache: new InMemoryToolResultCache(),
            hooks: hookRunner,
            artifacts: artifactStore,
            realPaths: new RealPathResolver());
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
        registry.Register(new CSharpAnalysisTool());
        registry.Register(new EnterPlanModeTool());
        registry.Register(new ExitPlanModeTool());
        registry.Register(new ReadMemoryTool());
        registry.Register(new WriteMemoryTool());
        registry.Register(new ReadArtifactTool(artifactStore));

        // MCP servers (if any) — spawn each, discover tools, register them as mcp__name__tool.
        // Skipped for an untrusted directory: spawning is arbitrary process execution.
        var mcpManager = new McpServerManager();
        if (trusted && projectConfig?.Mcp?.Servers is { Count: > 0 } mcpServers)
            await mcpManager.StartAsync(mcpServers, registry, default);

        // LSP servers (if any) — spawn each, run initialize, register four tools per server:
        // lsp__{name}__{hover|definition|references|open}. Also skipped when untrusted.
        var lspManager = new LspServerManager();
        if (trusted && projectConfig?.Lsp?.Servers is { Count: > 0 } lspServers)
            await lspManager.StartAsync(lspServers, registry, workingDirectory: workingDir, default);

        // Sub-agents — restricted-tool roles that the main agent can spawn for sub-tasks.
        var agents = new AgentRegistry();
        agents.Register(new AgentDefinition(
            "Explore",
            ["ReadFile", "Glob", "Grep", "CSharpAnalysis", "ReadMemory"],
            "You are an Explore sub-agent. You may only read and search the workspace; report what you find concisely."));
        agents.Register(new AgentDefinition(
            "Plan",
            ["ReadFile", "Glob", "Grep", "CSharpAnalysis", "ReadMemory", "EnterPlanMode", "ExitPlanMode"],
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
        if (int.TryParse(Environment.GetEnvironmentVariable("FREE_SESSION_ITERATIONS"), out var sit) && sit > 0)
            state.SessionIterationLimit = sit;

        // Optional workspace file watcher — opt-in via FREE_WATCH_FILES=1. When enabled, external
        // edits made between turns are surfaced to the model as a "files changed" notice prepended
        // to the next user turn.
        WorkspaceFileWatcher? fileWatcher = null;
        if (Environment.GetEnvironmentVariable("FREE_WATCH_FILES") is "1" or "true")
        {
            try { fileWatcher = new WorkspaceFileWatcher(workingDir); }
            catch (Exception ex) { Console.Error.WriteLine($"[freeagent] file watcher disabled: {ex.Message}"); }
        }

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

        // Optional pinned-bottom status bar (FREE_STATUS_BAR=1). When disabled this is a no-op.
        using var statusBar = new StatusBar();
        statusBar.Render(state, model, providerName);

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
                // /run <playbook> [args] expands into a normal user message and falls through.
                if (input.StartsWith("/run ", StringComparison.Ordinal) || input == "/run")
                {
                    var playbooks = Playbooks.LoadAll(workingDir);
                    var tokens = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (tokens.Length < 2)
                    {
                        Console.WriteLine(playbooks.Count == 0
                            ? "No playbooks found in .freeagent/playbooks or ~/.config/freeagent/playbooks."
                            : $"Available playbooks: {string.Join(", ", playbooks.Keys.OrderBy(n => n, StringComparer.Ordinal))}");
                        continue;
                    }
                    var rendered = Playbooks.Render(playbooks, tokens[1], tokens.Skip(2).ToArray());
                    if (rendered is null)
                    {
                        Console.WriteLine($"No playbook '{tokens[1]}'. Available: {string.Join(", ", playbooks.Keys.OrderBy(n => n, StringComparer.Ordinal))}");
                        continue;
                    }
                    Console.WriteLine($"[Running playbook '{tokens[1]}']");
                    input = rendered; // fall through to the normal turn dispatch
                }
                else
                {
                    HostCommands.Handle(input, state, model, diagnostics);
                    continue;
                }
            }

            // Prepend a "files changed externally" notice when the watcher saw any edits since the
            // last turn. The model sees the notice as part of the user turn, so the next response
            // can address the new state of the workspace.
            if (fileWatcher is not null)
            {
                var changes = fileWatcher.Drain();
                if (WorkspaceFileWatcher.RenderNotice(changes) is { } notice)
                    input = notice + "\n\n" + input;
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

            // Repaint the status row after each turn so message counts, iterations, and plan mode
            // reflect what just happened.
            statusBar.Render(state, model, providerName);
        }

        fileWatcher?.Dispose();
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
              freeagent [options]            Start the REPL in the current directory.
              freeagent setup                Run the interactive provider-config wizard.
              freeagent trust                Trust the current directory's .freeagent config.

            Run the REPL from the directory you want the agent to work in; that directory is its
            sandbox. First time? Run 'freeagent setup' to pick a provider and write the config.

            A project's .freeagent config can run code on launch (hooks, MCP/LSP servers) and grant
            extra permissions. The first time you open such a project you're asked to trust it; until
            then those run only after you approve. Use 'freeagent trust' or --trust to pre-approve.

            OPTIONS
              -h, --help        Show this help and exit.
                  --version     Show the version and exit.
              -v, --verbose     Stream the model's reasoning (dimmed) and per-turn token usage.
                  --trust       Trust this directory's .freeagent config for this run.
                  --resume [id] Resume the session in ./session.jsonl (optionally requiring its id).

            PROMPT COMMANDS (during REPL — type /help for the full list)
              /plan [on|off]    Toggle plan mode (only read-only tools run).
              /doctor           Print provider / model / tool inventory.
              /commands [q]     Fuzzy command palette.
              exit | quit       End the session (also saved on Ctrl+D / EOF).
              Ctrl+C            Cancel the current turn without quitting.

            CONFIGURATION (precedence: provider-specific env > config section > legacy > default)
              FREEPROVIDER       Active provider: openai (default) / anthropic / azure / ollama / bedrock / vertex.
              FREEMODEL          Model name (provider-agnostic env override).
              User config:       {ProviderConfig.ConfigPath()}  (written by 'freeagent setup')
              Permissions:       ./.freeagent/config.json  (allow/deny rules)
              See docs/usage.md for the per-provider env-var matrix.
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

    /// <summary>Path of the project config (<c>$FREEAGENT_CONFIG</c> or <c>.freeagent/config.json</c>).</summary>
    private static string ProjectConfigPath(string workingDir)
    {
        var path = Environment.GetEnvironmentVariable("FREEAGENT_CONFIG");
        return string.IsNullOrWhiteSpace(path) ? Path.Combine(workingDir, ".freeagent", "config.json") : path;
    }

    /// <summary>
    /// Parses <c>.freeagent/config.json</c> (or <c>$FREEAGENT_CONFIG</c>) without applying it — the
    /// caller decides what to apply based on trust. A missing file returns null; a malformed one is a
    /// non-fatal warning that never blocks startup.
    /// </summary>
    private static PermissionConfig? ParseProjectConfig(string workingDir)
    {
        var path = ProjectConfigPath(workingDir);
        if (!File.Exists(path))
            return null;

        try
        {
            return PermissionConfig.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning: ignoring config '{path}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Decides whether the project's executable/privilege config should be honored. True when the
    /// config has nothing privileged, when trust is forced (<c>--trust</c> / <c>FREEAGENT_TRUST</c>),
    /// when the directory is already remembered as trusted, or when the user approves the prompt.
    /// </summary>
    private static bool ResolveTrust(PermissionConfig? config, string workingDir, bool trustFlag)
    {
        if (config is null)
            return true;

        var requests = ProjectTrust.DescribeRequests(config);
        if (requests.Count == 0)
            return true; // only deny rules / nothing executable — no trust decision needed

        if (trustFlag || Environment.GetEnvironmentVariable("FREEAGENT_TRUST") is "1" or "true")
            return true;

        if (ProjectTrust.IsTrusted(workingDir))
            return true;

        return PromptForTrust(workingDir, requests);
    }

    /// <summary>
    /// Prompts the user to trust the current directory's executable config. Fails closed when stdin
    /// isn't interactive (so an automated/piped run never silently executes a cloned repo's config).
    /// </summary>
    private static bool PromptForTrust(string workingDir, IReadOnlyList<string> requests)
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine(
                $"[freeagent] {workingDir} has an untrusted .freeagent config; skipping its hooks, MCP/LSP servers, "
                + "and allow-rules (non-interactive). Run 'freeagent trust' here to enable them.");
            return false;
        }

        Console.WriteLine();
        Console.WriteLine("⚠ This project's .freeagent config wants to:");
        foreach (var request in requests)
            Console.WriteLine($"    • {request}");
        Console.Write("Trust this directory? [y]es once / [a]lways / [N]o › ");

        switch (Console.In.ReadLine()?.Trim().ToLowerInvariant())
        {
            case "a" or "always":
                ProjectTrust.Trust(workingDir);
                Console.WriteLine($"  ✓ Trusted {workingDir} (saved).");
                return true;
            case "y" or "yes" or "o" or "once":
                return true;
            default:
                Console.WriteLine("  Not trusted — hooks, MCP/LSP servers, and allow-rules are disabled this session.");
                return false;
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
