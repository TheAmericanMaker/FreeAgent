using FluentAssertions;
using FreeAgent.Host;

namespace FreeAgent.Kernel.Tests.Host;

public sealed class ProviderConfigTests
{
    // ── Resolve precedence (pure) ────────────────────────────────────────────

    [Fact]
    public void ResolvePrefersEnvOverFileOverDefault()
    {
        ProviderConfig.Resolve("env", "file", "default").Should().Be("env");
        ProviderConfig.Resolve(null, "file", "default").Should().Be("file");
        ProviderConfig.Resolve(null, null, "default").Should().Be("default");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveTreatsBlankAsAbsent(string blank)
    {
        ProviderConfig.Resolve(blank, "file", "default").Should().Be("file");
        ProviderConfig.Resolve(null, blank, "default").Should().Be("default");
    }

    // ── Load (file parsing) ──────────────────────────────────────────────────

    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "freeagent-tests", Guid.NewGuid().ToString("N") + ".json");

        public TempFile(string content)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, content);
        }

        public void Dispose()
        {
            try { File.Delete(Path); } catch (IOException) { }
        }
    }

    [Fact]
    public void LoadMissingFileYieldsEmptyConfig()
    {
        var config = ProviderConfig.Load(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "freeagent-no-such-" + Guid.NewGuid().ToString("N") + ".json"));
        config.BaseUrl.Should().BeNull();
        config.ApiKey.Should().BeNull();
        config.Model.Should().BeNull();
    }

    [Fact]
    public void LoadReadsBaseUrlModelAndKey()
    {
        using var file = new TempFile("""{ "baseUrl": "http://localhost:11434/v1", "model": "qwen", "apiKey": "k" }""");
        var config = ProviderConfig.Load(file.Path);
        config.BaseUrl.Should().Be("http://localhost:11434/v1");
        config.Model.Should().Be("qwen");
        config.ApiKey.Should().Be("k");
    }

    [Fact]
    public void LoadToleratesCommentsAndTrailingCommas()
    {
        using var file = new TempFile("""
            {
              // local model server
              "baseUrl": "http://localhost:1234/v1",
              "model": "local",
            }
            """);
        var config = ProviderConfig.Load(file.Path);
        config.BaseUrl.Should().Be("http://localhost:1234/v1");
        config.Model.Should().Be("local");
    }

    [Fact]
    public void LoadMalformedFileYieldsEmptyConfigWithoutThrowing()
    {
        using var file = new TempFile("{ not json");
        var config = ProviderConfig.Load(file.Path);
        config.BaseUrl.Should().BeNull();
    }

    [Fact]
    public void VertexSettingsUseApiVersionFromProviderSectionAsLocation()
    {
        using var file = new TempFile("""
            {
              "provider": "vertex",
              "vertex": {
                "baseUrl": "my-project",
                "apiVersion": "europe-west1",
                "model": "claude-3-7-sonnet@20250219"
              }
            }
            """);

        var config = ProviderConfig.Load(file.Path);
        var settings = config.SettingsFor("vertex");

        settings.BaseUrl.Should().Be("my-project");
        settings.ApiVersion.Should().Be("europe-west1");
        settings.Model.Should().Be("claude-3-7-sonnet@20250219");
    }
}
