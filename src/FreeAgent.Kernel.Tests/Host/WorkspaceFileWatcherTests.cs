using FluentAssertions;
using FreeAgent.Host;

namespace FreeAgent.Kernel.Tests.Host;

public sealed class WorkspaceFileWatcherTests
{
    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "freeagent-watch-tests", Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void DrainOnFreshWatcherReturnsEmpty()
    {
        using var dir = new TempDir();
        using var watcher = new WorkspaceFileWatcher(dir.Root);
        watcher.Drain().Should().BeEmpty();
    }

    [Fact]
    public void RecordAddsRelativePathOnDrain()
    {
        using var dir = new TempDir();
        using var watcher = new WorkspaceFileWatcher(dir.Root);

        // Use the internal Record entrypoint so the test doesn't race FileSystemWatcher timing.
        watcher.Record(Path.Combine(dir.Root, "src", "Foo.cs"));

        var drained = watcher.Drain();
        drained.Should().ContainSingle().Which.Should().Be("src/Foo.cs");
    }

    [Fact]
    public void DrainClearsTheBuffer()
    {
        using var dir = new TempDir();
        using var watcher = new WorkspaceFileWatcher(dir.Root);

        watcher.Record(Path.Combine(dir.Root, "a.cs"));
        watcher.Drain().Should().ContainSingle();
        watcher.Drain().Should().BeEmpty();
    }

    [Fact]
    public void RepeatedRecordOfSamePathDedupes()
    {
        using var dir = new TempDir();
        using var watcher = new WorkspaceFileWatcher(dir.Root);

        var p = Path.Combine(dir.Root, "x.cs");
        watcher.Record(p);
        watcher.Record(p);
        watcher.Record(p);

        watcher.Drain().Should().HaveCount(1);
    }

    [Fact]
    public void NoiseDirectoriesAreFilteredOut()
    {
        using var dir = new TempDir();
        using var watcher = new WorkspaceFileWatcher(dir.Root);

        watcher.Record(Path.Combine(dir.Root, ".git", "HEAD"));
        watcher.Record(Path.Combine(dir.Root, "node_modules", "lib.js"));
        watcher.Record(Path.Combine(dir.Root, "src", "bin", "out.exe"));
        watcher.Record(Path.Combine(dir.Root, "src", "obj", "Foo.dll"));
        watcher.Record(Path.Combine(dir.Root, "src", "Real.cs")); // keeper

        watcher.Drain().Should().ContainSingle().Which.Should().Be("src/Real.cs");
    }

    [Fact]
    public void DrainIsSortedDeterministically()
    {
        using var dir = new TempDir();
        using var watcher = new WorkspaceFileWatcher(dir.Root);

        watcher.Record(Path.Combine(dir.Root, "z.cs"));
        watcher.Record(Path.Combine(dir.Root, "a.cs"));
        watcher.Record(Path.Combine(dir.Root, "m.cs"));

        watcher.Drain().Should().Equal("a.cs", "m.cs", "z.cs");
    }

    [Fact]
    public void RenderNoticeReturnsNullForEmpty()
    {
        WorkspaceFileWatcher.RenderNotice([]).Should().BeNull();
    }

    [Fact]
    public void RenderNoticeFormatsBulletsAndCapsOverflow()
    {
        var paths = Enumerable.Range(1, 15).Select(i => $"f{i}.cs").ToArray();
        var notice = WorkspaceFileWatcher.RenderNotice(paths, cap: 10);

        notice.Should().NotBeNull();
        notice!.Should().StartWith("[freeagent] Files changed externally");
        notice.Should().Contain("- f1.cs").And.Contain("- f10.cs").And.NotContain("- f11.cs");
        notice.Should().Contain("(5 more)");
    }

    [Fact]
    public async Task RealFileSystemWatcherFiresOnFileCreate()
    {
        using var dir = new TempDir();
        using var watcher = new WorkspaceFileWatcher(dir.Root);

        var file = Path.Combine(dir.Root, "live.cs");
        await File.WriteAllTextAsync(file, "hi");

        // FileSystemWatcher events are delivered on a worker thread; poll briefly.
        IReadOnlyList<string> drained = [];
        for (var i = 0; i < 20; i++)
        {
            drained = watcher.Drain();
            if (drained.Count > 0) break;
            await Task.Delay(50);
            // Re-record any events that arrive after Drain by drainable means: the loop re-checks.
        }

        drained.Should().Contain("live.cs");
    }
}
