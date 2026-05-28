using FreeAgent.Server;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<ProviderFactory>();

var app = builder.Build();

// Optional API key gate (`FREEAGENT_SERVER_API_KEY`). When set, every request must include
// `Authorization: Bearer <key>` or be rejected. When empty (the default), the server is open —
// suitable for localhost-only use behind a loopback bind, not for public exposure.
var requiredApiKey = Environment.GetEnvironmentVariable("FREEAGENT_SERVER_API_KEY");
if (!string.IsNullOrEmpty(requiredApiKey))
{
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Headers.TryGetValue("Authorization", out var auth)
            && auth.ToString() is { } header
            && header.StartsWith("Bearer ", StringComparison.Ordinal)
            && string.Equals(header[7..], requiredApiKey, StringComparison.Ordinal))
        {
            await next();
            return;
        }
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("unauthorized");
    });
}

SessionEndpoints.Map(app);

app.Run();

/// <summary>Exposes the entrypoint to <c>WebApplicationFactory</c>-based tests.</summary>
public partial class Program;
