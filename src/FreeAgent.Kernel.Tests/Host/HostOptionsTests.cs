using FluentAssertions;
using FreeAgent.Host;

namespace FreeAgent.Kernel.Tests.Host;

public sealed class HostOptionsTests
{
    [Fact]
    public void DefaultsWhenNoArgs()
    {
        var options = HostOptions.Parse([]);
        options.Verbose.Should().BeFalse();
        options.Resume.Should().BeFalse();
        options.ResumeId.Should().BeNull();
        options.Help.Should().BeFalse();
        options.Version.Should().BeFalse();
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void HelpFlagRecognised(string flag)
    {
        HostOptions.Parse([flag]).Help.Should().BeTrue();
    }

    [Fact]
    public void VersionFlagRecognised()
    {
        HostOptions.Parse(["--version"]).Version.Should().BeTrue();
    }

    [Theory]
    [InlineData("--verbose")]
    [InlineData("-v")]
    public void VerboseFlagRecognised(string flag)
    {
        HostOptions.Parse([flag]).Verbose.Should().BeTrue();
    }

    [Fact]
    public void ResumeWithoutIdMeansLatest()
    {
        var options = HostOptions.Parse(["--resume"]);
        options.Resume.Should().BeTrue();
        options.ResumeId.Should().BeNull();
    }

    [Fact]
    public void ResumeWithIdCapturesId()
    {
        var options = HostOptions.Parse(["--resume", "abcd1234"]);
        options.Resume.Should().BeTrue();
        options.ResumeId.Should().Be("abcd1234");
    }

    [Fact]
    public void ResumeFollowedByFlagDoesNotConsumeFlagAsId()
    {
        var options = HostOptions.Parse(["--resume", "--verbose"]);
        options.Resume.Should().BeTrue();
        options.ResumeId.Should().BeNull();
        options.Verbose.Should().BeTrue();
    }

    [Fact]
    public void FlagsCombineRegardlessOfOrder()
    {
        var options = HostOptions.Parse(["-v", "--resume", "sess-1"]);
        options.Verbose.Should().BeTrue();
        options.Resume.Should().BeTrue();
        options.ResumeId.Should().Be("sess-1");
    }

    [Fact]
    public void DefaultSubcommandIsRepl()
    {
        HostOptions.Parse([]).Subcommand.Should().Be(HostSubcommand.Repl);
    }

    [Fact]
    public void SetupSubcommandIsRecognized()
    {
        HostOptions.Parse(["setup"]).Subcommand.Should().Be(HostSubcommand.Setup);
        HostOptions.Parse(["SETUP"]).Subcommand.Should().Be(HostSubcommand.Setup);
    }

    [Fact]
    public void SubcommandStillParsesTrailingFlags()
    {
        var options = HostOptions.Parse(["setup", "--verbose"]);
        options.Subcommand.Should().Be(HostSubcommand.Setup);
        options.Verbose.Should().BeTrue();
    }

    [Fact]
    public void UnknownLeadingPositionalDoesNotBecomeASubcommand()
    {
        // Falls through to flag parsing; subcommand stays at the default.
        HostOptions.Parse(["whatever"]).Subcommand.Should().Be(HostSubcommand.Repl);
    }

    [Fact]
    public void TrustSubcommandIsRecognized()
    {
        HostOptions.Parse(["trust"]).Subcommand.Should().Be(HostSubcommand.Trust);
        HostOptions.Parse(["TRUST"]).Subcommand.Should().Be(HostSubcommand.Trust);
    }

    [Fact]
    public void TrustFlagRecognised()
    {
        var options = HostOptions.Parse(["--trust"]);
        options.Trust.Should().BeTrue();
        options.Subcommand.Should().Be(HostSubcommand.Repl);
    }

    [Fact]
    public void TrustDefaultsFalse()
    {
        HostOptions.Parse([]).Trust.Should().BeFalse();
    }
}
