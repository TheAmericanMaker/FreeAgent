using System.Text.Json;
using FreeAgent.Kernel;

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
        var artifactStore = new InMemoryArtifactStore();
        var pipeline = new ToolPipeline(
            registry,
            permissions,
            approver: null,            // server context: prompting a human isn't available; everything goes through engine rules
            cache: new InMemoryToolResultCache(),
            hooks: null,
            artifacts: artifactStore);
        var store = new JsonlSessionStore();
        var state = new SessionState(id, workingDir, DateTimeOffset.UtcNow);
        var runtime = new SessionRuntime(provider, registry, pipeline, store, new NullEventSink(), state);
        return new SessionRegistry.SessionEntry(state, runtime, workingDir);
    }
}

public sealed record CreateSessionRequest(string? WorkingDirectory);
public sealed record TurnRequest(string? UserInput);
