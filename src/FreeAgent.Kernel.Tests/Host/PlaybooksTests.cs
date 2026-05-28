using FluentAssertions;
using FreeAgent.Host;

namespace FreeAgent.Kernel.Tests.Host;

public sealed class PlaybooksTests
{
    private sealed class TempProject : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "freeagent-tests", Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    [Fact]
    public void LoadAllDiscoversProjectPlaybooks()
    {
        using var work = new TempProject();
        var dir = System.IO.Path.Combine(work.Path, ".freeagent", "playbooks");
        Directory.CreateDirectory(dir);
        File.WriteAllText(System.IO.Path.Combine(dir, "commit.md"), "Write a commit for {{arg1}}.");
        File.WriteAllText(System.IO.Path.Combine(dir, "review.md"), "Review the current branch.");

        var loaded = Playbooks.LoadAll(work.Path);

        loaded.Keys.Should().BeEquivalentTo("commit", "review");
        loaded["commit"].Should().Be("Write a commit for {{arg1}}.");
    }

    [Fact]
    public void LoadAllReturnsEmptyWhenNoDirectoryExists()
    {
        using var work = new TempProject();
        Playbooks.LoadAll(work.Path).Should().BeEmpty();
    }

    [Fact]
    public void RenderSubstitutesPositionalArguments()
    {
        var book = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["commit"] = "Write a commit message for the change to {{arg1}} (impact: {{arg2}}).",
        };

        var rendered = Playbooks.Render(book, "commit", ["src/Foo.cs", "no behavior change"]);

        rendered.Should().Be("Write a commit message for the change to src/Foo.cs (impact: no behavior change).");
    }

    [Fact]
    public void RenderUnknownPlaybookReturnsNull()
    {
        var book = new Dictionary<string, string>(StringComparer.Ordinal);
        Playbooks.Render(book, "missing", []).Should().BeNull();
    }

    [Fact]
    public void RenderLeavesUnusedPlaceholdersAlone()
    {
        var book = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x"] = "first={{arg1}} second={{arg2}}",
        };

        Playbooks.Render(book, "x", ["A"]).Should().Be("first=A second={{arg2}}");
    }
}
