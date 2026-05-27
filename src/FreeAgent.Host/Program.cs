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
        var verbose = args.Contains("--verbose") || args.Contains("-v");

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
        var pipeline = new ToolPipeline(registry, permissions);
        var fs = new LinuxAtomicFileSystem();
        var store = new JsonlSessionStore(fs);
        var events = new ConsoleEventSink(verbose);

        // Register real tool adapters
        registry.Register(new ReadFileTool());
        registry.Register(new WriteFileTool());
        registry.Register(new ProcessExecTool());

        // ── session state ────────────────────────────────────────
        var sessionId = Guid.NewGuid().ToString()[..8];
        var state = new SessionState(sessionId, workingDir, DateTimeOffset.UtcNow);
        var runtime = new SessionRuntime(provider, registry, pipeline, store, events, state);

        // ── launch ─────────────────────────────────────────────────
        PrintBanner();
        Console.WriteLine($"Session: {sessionId} | Model: {model} | Working directory: {workingDir}");
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
