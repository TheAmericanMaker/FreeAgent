using System.Text.Json;
using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Persistence;

public sealed class JsonlSessionStoreTests
{
    [Fact]
    public async Task SaveUsesInjectedAtomicFileSystemWithDirectoryFsync()
    {
        var fs = new RecordingAtomicFileSystem();
        var store = new JsonlSessionStore(fs, "/tmp/freeagent/session.jsonl");
        var state = new SessionState("session-1", "/tmp/work", DateTimeOffset.Parse("2026-05-25T00:00:00Z"));

        await store.SaveAsync(state, CancellationToken.None);

        fs.Operations.Should().Equal("create-temp", "write-temp", "fsync-temp", "rename", "fsync-directory");
    }

    [Fact]
    public async Task SaveUsesUniqueTempPathsInSameDirectoryAsTarget()
    {
        var fs = new RecordingAtomicFileSystem();
        var targetPath = "/tmp/freeagent/session.jsonl";
        var store = new JsonlSessionStore(fs, targetPath);
        var state = new SessionState("session-1", "/tmp/work", DateTimeOffset.Parse("2026-05-25T00:00:00Z"));

        await store.SaveAsync(state, CancellationToken.None);
        await store.SaveAsync(state, CancellationToken.None);

        fs.TempPaths.Should().HaveCount(2);
        fs.TempPaths[0].Should().NotBe(fs.TempPaths[1]);
        fs.TempPaths.Select(Path.GetDirectoryName).Should().OnlyContain(directory => directory == Path.GetDirectoryName(targetPath));
    }

    [Fact]
    public async Task RealSaveLoadRoundTripPreservesSessionAndMessagesFromDisk()
    {
        var directory = Directory.CreateTempSubdirectory("freeagent-jsonl-");
        var path = Path.Combine(directory.FullName, "session.jsonl");
        var startedAt = DateTimeOffset.Parse("2026-05-25T00:00:00Z");
        var messageAt = DateTimeOffset.Parse("2026-05-25T00:00:01Z");
        var state = new SessionState("session-1", directory.FullName, startedAt);
        state.Messages.Add(new Message(MessageRole.User, "hello", Timestamp: messageAt));
        state.Messages.Add(new Message(
            MessageRole.Assistant,
            "calling tool",
            [new ToolCall("call-1", "echo", "{\"value\":\"abc\"}")],
            Timestamp: messageAt));
        state.Messages.Add(new Message(MessageRole.Tool, "echo:abc", ToolCallId: "call-1", ToolName: "echo", Timestamp: messageAt));

        await new JsonlSessionStore(path: path).SaveAsync(state, CancellationToken.None);
        var loaded = await new JsonlSessionStore(path: path).LoadAsync("session-1", CancellationToken.None);

        loaded.SessionId.Should().Be("session-1");
        loaded.WorkingDirectory.Should().Be(directory.FullName);
        loaded.StartedAt.Should().Be(startedAt);
        loaded.Messages.Should().HaveCount(3);
        loaded.Messages.Select(m => m.Role).Should().Equal(MessageRole.User, MessageRole.Assistant, MessageRole.Tool);
        loaded.Messages[0].Content.Should().Be("hello");
        loaded.Messages[0].Timestamp.Should().Be(messageAt);
        loaded.Messages[1].ToolCalls.Should().ContainSingle().Which.Should().Be(new ToolCall("call-1", "echo", "{\"value\":\"abc\"}"));
        loaded.Messages[2].ToolCallId.Should().Be("call-1");
        loaded.Messages[2].ToolName.Should().Be("echo");
        loaded.Messages[2].Content.Should().Be("echo:abc");
    }

    [Fact]
    public async Task LoadMissingFileThrowsClearException()
    {
        var directory = Directory.CreateTempSubdirectory("freeagent-jsonl-");
        var path = Path.Combine(directory.FullName, "missing.jsonl");
        var store = new JsonlSessionStore(path: path);

        var act = async () => await store.LoadAsync("session-1", CancellationToken.None);

        await act.Should().ThrowAsync<FileNotFoundException>().WithMessage("*Session JSONL file not found*missing.jsonl*");
    }

    [Fact]
    public async Task LoadEmptyJsonlThrowsClearFormatException()
    {
        var directory = Directory.CreateTempSubdirectory("freeagent-jsonl-");
        var path = Path.Combine(directory.FullName, "empty.jsonl");
        await File.WriteAllTextAsync(path, string.Empty, CancellationToken.None);
        var store = new JsonlSessionStore(path: path);

        var act = async () => await store.LoadAsync("session-1", CancellationToken.None);

        await act.Should().ThrowAsync<JsonException>().WithMessage("*empty*");
    }

    [Fact]
    public async Task InvalidHeaderThrowsClearFormatException()
    {
        var directory = Directory.CreateTempSubdirectory("freeagent-jsonl-");
        var path = Path.Combine(directory.FullName, "bad-header.jsonl");
        await File.WriteAllTextAsync(path, "{\"content\":\"not a session header\"}\n", CancellationToken.None);
        var store = new JsonlSessionStore(path: path);

        var act = async () => await store.LoadAsync("session-1", CancellationToken.None);

        await act.Should().ThrowAsync<JsonException>().WithMessage("*line 1*session_id*working_directory*");
    }

    [Fact]
    public async Task MalformedMessageLineIdentifiesLineNumber()
    {
        var directory = Directory.CreateTempSubdirectory("freeagent-jsonl-");
        var path = Path.Combine(directory.FullName, "bad-message.jsonl");
        await File.WriteAllTextAsync(path,
            "{\"session_id\":\"session-1\",\"started_at\":\"2026-05-25T00:00:00Z\",\"working_directory\":\"/tmp/work\"}\nnot-json\n",
            CancellationToken.None);
        var store = new JsonlSessionStore(path: path);

        var act = async () => await store.LoadAsync("session-1", CancellationToken.None);

        await act.Should().ThrowAsync<JsonException>().WithMessage("*line 2*");
    }

    [Fact]
    public async Task SessionIdMismatchIsRejected()
    {
        var directory = Directory.CreateTempSubdirectory("freeagent-jsonl-");
        var path = Path.Combine(directory.FullName, "session.jsonl");
        var state = new SessionState("session-a", directory.FullName, DateTimeOffset.Parse("2026-05-25T00:00:00Z"));
        await new JsonlSessionStore(path: path).SaveAsync(state, CancellationToken.None);
        var store = new JsonlSessionStore(path: path);

        var act = async () => await store.LoadAsync("session-b", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*session-a*session-b*");
    }
}
