using System.Text.Json;
using FluentAssertions;
using FreeAgent.Host;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Host;

public sealed class UndoTests
{
    private static JsonDocument Args(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value));

    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "freeagent-tests", Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    private static SessionState NewState(string root) => new("s", root, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task WriteFileSnapshotsPreviousContentAndUndoRestoresIt()
    {
        using var work = new TempWorkspace();
        var file = Path.Combine(work.Root, "a.txt");
        await File.WriteAllTextAsync(file, "original");
        var state = NewState(work.Root);
        var ctx = new ToolContext(state);

        await new WriteFileTool().ExecuteAsync(
            Args(new { path = "a.txt", content = "modified" }), ctx, CancellationToken.None);

        state.History.Count.Should().Be(1);
        (await File.ReadAllTextAsync(file)).Should().Be("modified");

        var status = HostCommands.Undo(state);

        status.Should().Contain("restored").And.Contain(file);
        (await File.ReadAllTextAsync(file)).Should().Be("original");
        state.History.Count.Should().Be(0);
    }

    [Fact]
    public async Task UndoOfFirstWriteToANewFileDeletesIt()
    {
        using var work = new TempWorkspace();
        var file = Path.Combine(work.Root, "new.txt");
        var state = NewState(work.Root);
        var ctx = new ToolContext(state);

        await new WriteFileTool().ExecuteAsync(
            Args(new { path = "new.txt", content = "fresh" }), ctx, CancellationToken.None);
        File.Exists(file).Should().BeTrue();

        var status = HostCommands.Undo(state);

        status.Should().Contain("deleted");
        File.Exists(file).Should().BeFalse();
    }

    [Fact]
    public async Task EditFileSnapshotsPreEditContentAndUndoRestoresIt()
    {
        using var work = new TempWorkspace();
        var file = Path.Combine(work.Root, "a.txt");
        await File.WriteAllTextAsync(file, "hello world");
        var state = NewState(work.Root);
        var ctx = new ToolContext(state);

        await new EditFileTool().ExecuteAsync(
            Args(new { path = "a.txt", old_string = "world", new_string = "FreeAgent" }), ctx, CancellationToken.None);
        (await File.ReadAllTextAsync(file)).Should().Be("hello FreeAgent");

        HostCommands.Undo(state);

        (await File.ReadAllTextAsync(file)).Should().Be("hello world");
    }

    [Fact]
    public async Task MultipleEditsUndoInLifoOrder()
    {
        using var work = new TempWorkspace();
        var file = Path.Combine(work.Root, "a.txt");
        await File.WriteAllTextAsync(file, "one");
        var state = NewState(work.Root);
        var ctx = new ToolContext(state);

        await new WriteFileTool().ExecuteAsync(Args(new { path = "a.txt", content = "two" }), ctx, CancellationToken.None);
        await new WriteFileTool().ExecuteAsync(Args(new { path = "a.txt", content = "three" }), ctx, CancellationToken.None);

        (await File.ReadAllTextAsync(file)).Should().Be("three");

        HostCommands.Undo(state);
        (await File.ReadAllTextAsync(file)).Should().Be("two");

        HostCommands.Undo(state);
        (await File.ReadAllTextAsync(file)).Should().Be("one");

        HostCommands.Undo(state).Should().Be("Nothing to undo.");
    }

    [Fact]
    public void UndoWithEmptyHistoryReturnsClearStatus()
    {
        HostCommands.Undo(new SessionState("s", "/tmp", DateTimeOffset.UnixEpoch))
            .Should().Be("Nothing to undo.");
    }
}
