using FreeAgent.Server;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<ProviderFactory>();
builder.Services.AddOpenApi();

// Safe-by-default networking. Bind to loopback unless FREEAGENT_SERVER_URLS / ASPNETCORE_URLS say
// otherwise, and refuse to expose a non-loopback bind without an API key — an open agent on a
// routable interface is remote code execution. (Under WebApplicationFactory tests the bind defaults
// to loopback and UseUrls is overridden by the test server, so this is inert there.)
var apiKey = Environment.GetEnvironmentVariable("FREEAGENT_SERVER_API_KEY");
var bindUrls = ServerSecurity.ResolveBindUrls(
    Environment.GetEnvironmentVariable("FREEAGENT_SERVER_URLS"),
    Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));
if (ServerSecurity.BindsBeyondLoopback(bindUrls) && string.IsNullOrEmpty(apiKey))
{
    Console.Error.WriteLine(
        $"freeagent-server: refusing to bind to a non-loopback address ({bindUrls}) without "
        + "FREEAGENT_SERVER_API_KEY set. Set the key, or bind to localhost.");
    return;
}
builder.WebHost.UseUrls(bindUrls);

var maxSessions = int.TryParse(Environment.GetEnvironmentVariable("FREEAGENT_SERVER_MAX_SESSIONS"), out var cap) && cap > 0
    ? cap
    : 256;

var app = builder.Build();

// OpenAPI spec at /openapi/v1.json — per ADR 0005, the protocol surface is contract-first; clients
// (TUI, editor, web) can regenerate from this document instead of mirroring hand-rolled types.
app.MapOpenApi();

// API-key gate. Open only when no key is configured (allowed only on the loopback bind enforced
// above). The token compare is constant-time to avoid a timing oracle on the key.
app.Use(async (ctx, next) =>
{
    if (ServerSecurity.TokenMatches(apiKey, ctx.Request.Headers.Authorization.ToString()))
    {
        await next();
        return;
    }
    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await ctx.Response.WriteAsync("unauthorized");
});

SessionEndpoints.Map(app, maxSessions);
ConfigEndpoints.Map(app);

app.Run();

/// <summary>Exposes the entrypoint to <c>WebApplicationFactory</c>-based tests.</summary>
public partial class Program;
