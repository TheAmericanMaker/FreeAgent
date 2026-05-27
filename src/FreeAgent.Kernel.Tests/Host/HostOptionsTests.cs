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
}
