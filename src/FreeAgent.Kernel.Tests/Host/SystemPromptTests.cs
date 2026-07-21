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
    public void DefaultPromptGroundsTheModelAndIncludesTheWorkingDirectory()
    {
        using var dir = new TempDir();

        var prompt = SystemPrompt.Compose(dir.Path);

        prompt.Should().Contain("FreeAgent");
        prompt.Should().Contain("approval dialog"); // the anti-hallucination guidance
        prompt.Should().Contain($"Working directory: {dir.Path}");
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
        prompt.Should().Contain($"Working directory: {dir.Path}"); // runtime context still included
    }

    [Fact]
    public void BlankOverrideFallsBackToTheDefault()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(System.IO.Path.Combine(dir.Path, ".freeagent"));
        File.WriteAllText(System.IO.Path.Combine(dir.Path, ".freeagent", "system.md"), "   \n  ");

        SystemPrompt.Compose(dir.Path).Should().Contain("autonomous coding agent");
    }

    [Fact]
    public void GitBranchReadFromHeadIsIncluded()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(System.IO.Path.Combine(dir.Path, ".git"));
        File.WriteAllText(System.IO.Path.Combine(dir.Path, ".git", "HEAD"), "ref: refs/heads/feature/awesome\n");

        var prompt = SystemPrompt.Compose(dir.Path);

        prompt.Should().Contain("Git branch: feature/awesome");
    }

    [Fact]
    public void DetachedHeadShowsShortShaWithMarker()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(System.IO.Path.Combine(dir.Path, ".git"));
        File.WriteAllText(System.IO.Path.Combine(dir.Path, ".git", "HEAD"), "abcdef0123456789\n");

        var prompt = SystemPrompt.Compose(dir.Path);

        prompt.Should().Contain("Git branch: detached @ abcdef0");
    }

    [Fact]
    public void NoGitDirectoryMeansNoGitSection()
    {
        using var dir = new TempDir();

        SystemPrompt.Compose(dir.Path).Should().NotContain("Git branch:");
    }

    [Fact]
    public void ProjectContextFileIsAppendedWhenPresent()
    {
        using var dir = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "CLAUDE.md"), "Conventions: use 2-space indent.");

        var prompt = SystemPrompt.Compose(dir.Path);

        prompt.Should().Contain("--- Project context (CLAUDE.md) ---");
        prompt.Should().Contain("use 2-space indent");
    }

    [Fact]
    public void ProjectContextFilesArePriorityOrdered()
    {
        using var dir = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "CLAUDE.md"), "claude content");
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "AGENTS.md"), "agents content");

        var prompt = SystemPrompt.Compose(dir.Path);

        prompt.Should().Contain("CLAUDE.md").And.Contain("claude content");
        prompt.Should().NotContain("agents content"); // first match wins
    }
}
