using System.Text.Json;
using FreeAgent.Kernel;
using FreeAgent.Host;

namespace FreeAgent.Server;

/// <summary>
/// HTTP endpoints exposing the kernel as a network service (per ADR 0005). Five endpoints:
/// <list type="bullet">
///   <item><c>POST /sessions</c> — create a session, return its id + working directory.</item>
///   <item><c>GET /sessions</c> — list active session ids.</item>
///   <item><c>GET /sessions/{id}</c> — full session state (messages, plan mode, tags, etc.).</item>
///   <item><c>POST /sessions/{id}/turns</c> — submit a user turn; SSE response streams the agent's
///         text and final result.</item>
///   <item><c>DELETE /sessions/{id}</c> — remove the session from the registry.</item>
/// </list>
/// Designed to be the wire surface for the future TUI client (Bun/opentui per ADR 0005) and any
/// other frontend: VS Code extension, ACP, web, Slack, GitHub. The server itself is intentionally
/// thin — auth + transport + session lookup; everything semantic lives in the kernel.
/// </summary>
public static class SessionEndpoints
{
    public static void Map(WebApplication app, int maxSessions = int.MaxValue)
    {
        app.MapPost("/sessions", (CreateSessionRequest? body, SessionRegistry registry, ProviderFactory factory) =>
        {
            // Cap live sessions so an (unauthenticated, loopback) client can't exhaust memory/handles.
            if (registry.Count >= maxSessions)
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);

            var workingDir = string.IsNullOrWhiteSpace(body?.WorkingDirectory)
                ? Directory.GetCurrentDirectory()
                : body!.WorkingDirectory!;
            var id = Guid.NewGuid().ToString("N")[..8];

            registry.GetOrAdd(id, _ => CreateEntry(id, workingDir, factory));
            return Results.Created($"/sessions/{id}", new { sessionId = id, workingDirectory = workingDir });
        });

        app.MapGet("/sessions", (SessionRegistry registry) => Results.Ok(registry.SessionIds()));

        app.MapGet("/sessions/{id}", (string id, SessionRegistry registry) =>
        {
            if (!registry.TryGet(id, out var entry)) return Results.NotFound();
            return Results.Ok(new
            {
                sessionId = entry.State.SessionId,
                workingDirectory = entry.WorkingDirectory,
                messageCount = entry.State.Messages.Count,
                planMode = entry.State.PlanMode,
                tags = entry.State.Tags.ToArray(),
                totalIterations = entry.State.TotalIterations,
            });
        });

        app.MapPost("/sessions/{id}/turns", async (string id, TurnRequest body, HttpContext ctx, SessionRegistry registry) =>
        {
            if (!registry.TryGet(id, out var entry)) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            if (string.IsNullOrEmpty(body?.UserInput)) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }

            // One turn at a time per session: the runtime + its SessionState can't be driven by two
            // concurrent requests (they'd race the event-sink swap and the shared message list).
            if (!entry.Gate.Wait(0)) { ctx.Response.StatusCode = StatusCodes.Status409Conflict; return; }

            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no"; // disable proxy buffering

            // The IEventSink callbacks are synchronous (void OnText, etc.) so HttpSseEventSink
            // writes to the response body synchronously. Allow it on this streaming response.
            var httpCtxFeature = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
            if (httpCtxFeature is not null)
                httpCtxFeature.AllowSynchronousIO = true;

            var streamingSink = new HttpSseEventSink(ctx.Response);
            entry.Runtime.SwapEventSink(streamingSink); // route the runtime's events into this turn's SSE stream

            try
            {
                var result = await entry.Runtime.RunTurnAsync(body.UserInput!, ctx.RequestAborted);
                await streamingSink.WriteEventAsync("done", JsonSerializer.Serialize(new
                {
                    finalText = result.FinalText,
                    doomLoopDetected = result.DoomLoopDetected,
                }));
            }
            catch (OperationCanceledException)
            {
                await streamingSink.WriteEventAsync("cancelled", "{}");
            }
            catch (Exception ex)
            {
                await streamingSink.WriteEventAsync("error", JsonSerializer.Serialize(new { message = ex.Message }));
            }
            finally
            {
                entry.Gate.Release();
            }
        });

        app.MapDelete("/sessions/{id}", (string id, SessionRegistry registry) =>
        {
            return registry.Remove(id, out _) ? Results.NoContent() : Results.NotFound();
        });
    }

    private static SessionRegistry.SessionEntry CreateEntry(string id, string workingDir, ProviderFactory factory)
    {
        var (provider, _, _) = factory.Create();
        var registry = new ToolRegistry();
        var permissions = new PermissionEngine();

        // Apply the project's .freeagent permission config, honoring workspace trust. A config that
        // wants to run code or widen permissions is honored only for a directory the user has trusted
        // (via POST /config/trust from the UI); otherwise only its deny-rules apply. Deny always applies.
        var projectConfig = LoadProjectConfig(workingDir);
        var trusted = projectConfig is null
            || ProjectTrust.DescribeRequests(projectConfig).Count == 0
            || ProjectTrust.IsTrusted(workingDir);
        projectConfig?.ApplyTo(permissions, includeGrants: trusted);

        var artifactStore = new InMemoryArtifactStore();
        var hooks = new HookRunner(trusted ? projectConfig?.Hooks : null, new BashShellExecutor());
        var pipeline = new ToolPipeline(
            registry,
            permissions,
            approver: null,            // server context: prompting a human isn't available; everything goes through engine rules
            cache: new InMemoryToolResultCache(),
            hooks: hooks,
            artifacts: artifactStore,
            realPaths: new RealPathResolver());

        RegisterBuiltinTools(registry, artifactStore);
        RegisterSubAgents(registry, provider, permissions);

        var store = new JsonlSessionStore();
        var state = new SessionState(id, workingDir, DateTimeOffset.UtcNow);
        // Same system prompt the CLI composes — without it the model has no operating instructions.
        state.Messages.Add(new Message(MessageRole.System, SystemPrompt.Compose(workingDir)));
        var runtime = new SessionRuntime(provider, registry, pipeline, store, new NullEventSink(), state);
        return new SessionRegistry.SessionEntry(state, runtime, workingDir);
    }

    /// <summary>Registers the same built-in tool set the host CLI exposes (kernel-resident adapters).</summary>
    private static void RegisterBuiltinTools(ToolRegistry registry, InMemoryArtifactStore artifactStore)
    {
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
    }

    /// <summary>Registers the four default sub-agent roles and the SpawnAgent tool, mirroring the host.</summary>
    private static void RegisterSubAgents(ToolRegistry registry, IProvider provider, PermissionEngine permissions)
    {
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

        // No ConsoleApprover in the server: sub-agent capability prompts resolve through engine rules.
        var subAgentRunner = new SubAgentRunner(provider, registry, permissions, agents, approver: null);
        registry.Register(new SpawnAgentTool(subAgentRunner, agents));
    }

    /// <summary>Parses <c>{workingDir}/.freeagent/config.json</c> (or <c>$FREEAGENT_CONFIG</c>); null if absent/malformed.</summary>
    private static PermissionConfig? LoadProjectConfig(string workingDir)
    {
        var path = Environment.GetEnvironmentVariable("FREEAGENT_CONFIG");
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(workingDir, ".freeagent", "config.json");
        if (!File.Exists(path)) return null;
        try { return PermissionConfig.Parse(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public sealed record CreateSessionRequest(string? WorkingDirectory);
public sealed record TurnRequest(string? UserInput);
