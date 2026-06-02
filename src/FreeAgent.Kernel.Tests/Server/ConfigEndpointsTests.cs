using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FreeAgent.Kernel.Tests.Server;

/// <summary>
/// HTTP surface tests for the config/setup endpoints (<see cref="global::FreeAgent.Server.ConfigEndpoints"/>).
/// These back the in-TUI setup flow, so we cover the provider/model metadata, the redacted config view,
/// the provider write round-trip (including key preservation), the offline branch of the connection
/// test, and the permissions + trust read/write. Each config-mutating test runs against its own
/// <see cref="WebApplicationFactory{TEntryPoint}"/> with an isolated <c>XDG_CONFIG_HOME</c> so the shared
/// provider config / trust store can't bleed between tests or into the developer's real config.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class ConfigEndpointsTests : IDisposable
{
    private readonly string _xdg = Path.Combine(Path.GetTempPath(), "freeagent-cfg-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<(string Key, string? Old)> _saved = [];

    public ConfigEndpointsTests()
    {
        // Isolate the user config + trust store, and clear provider env so file values aren't shadowed.
        Set("XDG_CONFIG_HOME", _xdg);
        foreach (var key in new[]
        {
            "FREEAGENT_SERVER_API_KEY", "FREEAGENT_CONFIG", "FREEPROVIDER", "FREEMODEL",
            "OPENAI_API_KEY", "OPENAI_BASE_URL", "ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL",
        })
            Set(key, null);
    }

    private void Set(string key, string? value)
    {
        _saved.Add((key, Environment.GetEnvironmentVariable(key)));
        Environment.SetEnvironmentVariable(key, value);
    }

    public void Dispose()
    {
        foreach (var (key, old) in _saved) Environment.SetEnvironmentVariable(key, old);
        try { if (Directory.Exists(_xdg)) Directory.Delete(_xdg, recursive: true); } catch { /* best-effort */ }
    }

    private static WebApplicationFactory<global::Program> NewApp() => new();

    [Fact]
    public async Task GetProviders_ListsEveryProviderWithFieldSchema()
    {
        using var app = NewApp();
        var doc = await app.CreateClient().GetFromJsonAsync<JsonElement>("/providers");

        var ids = doc.EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToArray();
        ids.Should().BeEquivalentTo("openai", "anthropic", "azure", "ollama", "bedrock", "vertex");

        var openai = doc.EnumerateArray().First(p => p.GetProperty("id").GetString() == "openai");
        openai.GetProperty("requiresApiKey").GetBoolean().Should().BeTrue();
        openai.GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("slot").GetString())
            .Should().Contain(["apiKey", "baseUrl", "model"]);

        var ollama = doc.EnumerateArray().First(p => p.GetProperty("id").GetString() == "ollama");
        ollama.GetProperty("requiresApiKey").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("anthropic", "claude")]
    [InlineData("openai", "gpt")]
    public async Task GetModels_FiltersByProviderWireApi(string provider, string expectedIdSubstring)
    {
        using var app = NewApp();
        var doc = await app.CreateClient().GetFromJsonAsync<JsonElement>($"/models?provider={provider}");

        var ids = doc.EnumerateArray().Select(m => m.GetProperty("id").GetString()!).ToArray();
        ids.Should().NotBeEmpty();
        ids.Should().OnlyContain(id => id.Contains(expectedIdSubstring));
    }

    [Fact]
    public async Task PutProvider_ThenGetConfig_RoundTripsWithRedactedKey()
    {
        using var app = NewApp();
        var client = app.CreateClient();

        var put = await client.PutAsJsonAsync("/config/provider", new
        {
            provider = "openai",
            apiKey = "sk-secret-key-1234",
            model = "gpt-4o",
            setAsDefault = true,
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = await (await client.GetAsync("/config")).Content.ReadAsStringAsync();
        // The secret must never be echoed back to a client.
        raw.Should().NotContain("sk-secret-key-1234");

        var cfg = JsonSerializer.Deserialize<JsonElement>(raw);
        cfg.GetProperty("activeProvider").GetString().Should().Be("openai");
        var openai = cfg.GetProperty("providers").GetProperty("openai");
        openai.GetProperty("model").GetString().Should().Be("gpt-4o");
        openai.GetProperty("apiKeySet").GetBoolean().Should().BeTrue();
        openai.GetProperty("apiKeyHint").GetString().Should().EndWith("1234");
    }

    [Fact]
    public async Task PutProvider_WithoutApiKey_PreservesTheExistingKey()
    {
        using var app = NewApp();
        var client = app.CreateClient();

        await client.PutAsJsonAsync("/config/provider", new { provider = "openai", apiKey = "sk-keep-me-9876", model = "gpt-4o" });
        // Second write changes only the model and omits the key.
        await client.PutAsJsonAsync("/config/provider", new { provider = "openai", model = "gpt-4o-mini" });

        var openai = (await client.GetFromJsonAsync<JsonElement>("/config")).GetProperty("providers").GetProperty("openai");
        openai.GetProperty("model").GetString().Should().Be("gpt-4o-mini");
        openai.GetProperty("apiKeySet").GetBoolean().Should().BeTrue();
        openai.GetProperty("apiKeyHint").GetString().Should().EndWith("9876");
    }

    [Fact]
    public async Task PutProvider_UnknownProvider_Returns400()
    {
        using var app = NewApp();
        var resp = await app.CreateClient().PutAsJsonAsync("/config/provider", new { provider = "not-a-provider" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TestProvider_WithNoKey_FailsLocallyWithoutNetwork()
    {
        using var app = NewApp();
        var resp = await app.CreateClient().PostAsJsonAsync("/config/provider/test", new { provider = "openai", apiKey = "" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("ok").GetBoolean().Should().BeFalse();
        result.GetProperty("mode").GetString().Should().Be("fields");
    }

    [Fact]
    public async Task PermissionsRoundTrip_PreservesUnrelatedSections()
    {
        using var app = NewApp();
        var client = app.CreateClient();
        var dir = Path.Combine(_xdg, "proj");
        Directory.CreateDirectory(Path.Combine(dir, ".freeagent"));
        // Seed a config that also carries a non-permissions section (hooks) that must survive the edit.
        await File.WriteAllTextAsync(Path.Combine(dir, ".freeagent", "config.json"),
            """{ "hooks": { "sessionStart": [ { "run": "echo hi" } ] }, "denyTools": ["ProcessExec"] }""");

        var put = await client.PutAsJsonAsync("/config/permissions", new
        {
            dir,
            allowTools = new[] { "ReadFile" },
            denyTools = new[] { "ProcessExec" },
            allow = new[] { new { capability = "FileWriteCap", pattern = "*.md" } },
            deny = Array.Empty<object>(),
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var view = await client.GetFromJsonAsync<JsonElement>($"/config/permissions?dir={Uri.EscapeDataString(dir)}");
        view.GetProperty("allowTools").EnumerateArray().Select(x => x.GetString()).Should().Contain("ReadFile");
        view.GetProperty("allow").EnumerateArray().First().GetProperty("capability").GetString().Should().Be("FileWriteCap");
        view.GetProperty("knownCapabilities").EnumerateArray().Should().NotBeEmpty();

        // The hooks section the UI never touched must still be on disk.
        var onDisk = await File.ReadAllTextAsync(Path.Combine(dir, ".freeagent", "config.json"));
        onDisk.Should().Contain("sessionStart");
    }

    [Fact]
    public async Task Permissions_UnknownCapability_Returns400()
    {
        using var app = NewApp();
        var dir = Path.Combine(_xdg, "proj2");
        var resp = await app.CreateClient().PutAsJsonAsync("/config/permissions", new
        {
            dir,
            allow = new[] { new { capability = "NotARealCapability", pattern = (string?)null } },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Trust_StartsUntrusted_ThenTrustPersists()
    {
        using var app = NewApp();
        var client = app.CreateClient();
        var dir = Path.Combine(_xdg, "trust-proj");
        Directory.CreateDirectory(dir);

        var before = await client.GetFromJsonAsync<JsonElement>($"/config/trust?dir={Uri.EscapeDataString(dir)}");
        before.GetProperty("trusted").GetBoolean().Should().BeFalse();

        var post = await client.PostAsJsonAsync("/config/trust", new { dir });
        post.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await client.GetFromJsonAsync<JsonElement>($"/config/trust?dir={Uri.EscapeDataString(dir)}");
        after.GetProperty("trusted").GetBoolean().Should().BeTrue();
    }
}
