using FluentAssertions;
using FreeAgent.Host;

namespace FreeAgent.Kernel.Tests.Host;

public sealed class SystemPromptTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "freeagent-tests", Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    [Fact]
    public void DefaultPromptGroundsTheModelAndAppendsWorkingDirectory()
    {
        using var dir = new TempDir();

        var prompt = SystemPrompt.Compose(dir.Path);

        prompt.Should().Contain("FreeAgent");
        prompt.Should().Contain("approval dialog"); // the anti-hallucination guidance
        prompt.Should().EndWith($"Working directory: {dir.Path}");
    }

    [Fact]
    public void ProjectOverrideReplacesTheBasePrompt()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(System.IO.Path.Combine(dir.Path, ".freeagent"));
        File.WriteAllText(System.IO.Path.Combine(dir.Path, ".freeagent", "system.md"), "CUSTOM BASE PROMPT");

        var prompt = SystemPrompt.Compose(dir.Path);

        prompt.Should().StartWith("CUSTOM BASE PROMPT");
        prompt.Should().NotContain("autonomous coding agent"); // default base replaced
        prompt.Should().EndWith($"Working directory: {dir.Path}"); // runtime context still appended
    }

    [Fact]
    public void BlankOverrideFallsBackToTheDefault()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(System.IO.Path.Combine(dir.Path, ".freeagent"));
        File.WriteAllText(System.IO.Path.Combine(dir.Path, ".freeagent", "system.md"), "   \n  ");

        SystemPrompt.Compose(dir.Path).Should().Contain("autonomous coding agent");
    }
}
