using FluentAssertions;
using FreeAgent.Server;

namespace FreeAgent.Kernel.Tests.Server;

public sealed class ProviderProbeTests
{
    [Theory]
    // https to any host is allowed (legitimate cloud APIs).
    [InlineData("https://api.openai.com/v1/models")]
    [InlineData("https://internal.corp/v1/models")]
    // http only to loopback (a local OpenAI-compatible / Ollama server).
    [InlineData("http://localhost:11434/api/tags")]
    [InlineData("http://127.0.0.1:8080/v1/models")]
    [InlineData("http://[::1]:11434/api/tags")]
    public void AllowsHttpsAnywhereAndHttpToLoopback(string url)
    {
        ProviderProbe.IsAllowedTarget(url).Should().BeTrue();
    }

    [Theory]
    // Plaintext http to a non-loopback host — the SSRF path to internal services — is refused.
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.5/admin")]
    [InlineData("http://internal-service/v1/models")]
    [InlineData("ftp://example.com/x")]
    [InlineData("not-a-url")]
    [InlineData("")]
    [InlineData(null)]
    public void RefusesNonLoopbackHttpAndNonHttpSchemes(string? url)
    {
        ProviderProbe.IsAllowedTarget(url).Should().BeFalse();
    }
}
