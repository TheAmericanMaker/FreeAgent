using FluentAssertions;
using FreeAgent.Server;

namespace FreeAgent.Kernel.Tests.Server;

public sealed class ServerSecurityTests
{
    // ── ResolveBindUrls: FREEAGENT_SERVER_URLS > ASPNETCORE_URLS > loopback default ──

    [Fact]
    public void ResolveBindUrlsPrefersFreeagentThenAspnetThenDefault()
    {
        ServerSecurity.ResolveBindUrls("http://a:1", "http://b:2").Should().Be("http://a:1");
        ServerSecurity.ResolveBindUrls(null, "http://b:2").Should().Be("http://b:2");
        ServerSecurity.ResolveBindUrls("   ", "http://b:2").Should().Be("http://b:2");
        ServerSecurity.ResolveBindUrls(null, null).Should().Be(ServerSecurity.DefaultBind);
        ServerSecurity.DefaultBind.Should().Contain("127.0.0.1");
    }

    // ── BindsBeyondLoopback ──

    [Theory]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://localhost:5000")]
    [InlineData("https://[::1]:5000")]
    [InlineData("http://127.0.0.1:5000;http://localhost:5001")]
    [InlineData("")]
    public void LoopbackBindsAreNotBeyondLoopback(string urls)
    {
        ServerSecurity.BindsBeyondLoopback(urls).Should().BeFalse();
    }

    [Theory]
    [InlineData("http://0.0.0.0:5000")]
    [InlineData("http://+:5000")]
    [InlineData("http://*:5000")]
    [InlineData("http://[::]:5000")]
    [InlineData("http://192.168.1.10:5000")]
    [InlineData("https://example.com")]
    [InlineData("http://127.0.0.1:5000;http://0.0.0.0:6000")] // one wildcard in the list is enough
    public void PublicBindsAreBeyondLoopback(string urls)
    {
        ServerSecurity.BindsBeyondLoopback(urls).Should().BeTrue();
    }

    // ── TokenMatches (constant-time bearer compare) ──

    [Fact]
    public void TokenMatchesIsOpenWhenNoKeyConfigured()
    {
        ServerSecurity.TokenMatches(null, "anything").Should().BeTrue();
        ServerSecurity.TokenMatches("", null).Should().BeTrue();
    }

    [Fact]
    public void TokenMatchesRequiresTheCorrectBearerWhenKeyConfigured()
    {
        ServerSecurity.TokenMatches("s3cret", "Bearer s3cret").Should().BeTrue();
        ServerSecurity.TokenMatches("s3cret", "Bearer wrong").Should().BeFalse();
        ServerSecurity.TokenMatches("s3cret", null).Should().BeFalse();
        ServerSecurity.TokenMatches("s3cret", "").Should().BeFalse();
        ServerSecurity.TokenMatches("s3cret", "s3cret").Should().BeFalse(); // missing "Bearer " prefix
    }
}
