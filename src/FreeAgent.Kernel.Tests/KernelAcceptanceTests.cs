using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests;

public sealed class KernelAcceptanceTests
{
    [Fact]
    public async Task TextOnlyResponseEndsTheTurnAndSavesSession()
    {
        var harness = KernelHarness.Create(StreamScript.Text("hello"));

        var result = await harness.Runtime.RunTurnAsync("hi", CancellationToken.None);

        result.FinalText.Should().Be("hello");
        harness.Provider.Requests.Should().HaveCount(1);
        harness.Store.SaveCount.Should().Be(1);
        harness.State.Messages.Select(m => m.Role).Should().Equal(MessageRole.User, MessageRole.Assistant);
    }

    [Fact]
    public async Task ToolCallIsCollectedExecutedInjectedThenFinalTextEndsTheTurn()
    {
        var harness = KernelHarness.Create(
            StreamScript.ToolCall("call-1", "echo", "{\"value\":\"abc\"}"),
            StreamScript.Text("done"));
        harness.Registry.Register(new FakeTool("echo", args => ToolResult.Success("echo:" + args.RootElement.GetProperty("value").GetString())));

        var result = await harness.Runtime.RunTurnAsync("use echo", CancellationToken.None);

        result.FinalText.Should().Be("done");
        harness.Provider.Requests.Should().HaveCount(2);
        harness.State.Messages.Should().Contain(m => m.Role == MessageRole.Tool && m.ToolCallId == "call-1" && m.Content == "echo:abc");
        harness.Provider.Requests[1].Messages.Should().Contain(m => m.Role == MessageRole.Tool && m.Content == "echo:abc");
    }

    [Fact]
    public async Task DoomLoopDetectorTriggersAfterExactlyThreeIdenticalConsecutiveToolCallBatches()
    {
        var batch = StreamScript.ToolCall("same", "echo", "{\"value\":\"x\"}");
        var harness = KernelHarness.Create(batch, batch, batch, StreamScript.Text("recovered"));
        harness.Registry.Register(new FakeTool("echo", _ => ToolResult.Success("x")));

        var result = await harness.Runtime.RunTurnAsync("loop", CancellationToken.None);

        result.DoomLoopDetected.Should().BeTrue();
        harness.Provider.Requests.Should().HaveCount(4);
        harness.State.Messages.Should().Contain(m => m.Role == MessageRole.Assistant && m.Content.Contains("doom loop", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DoomLoopDetectorDoesNotTriggerAfterOnlyTwoIdenticalBatches()
    {
        var batch = StreamScript.ToolCall("same", "echo", "{\"value\":\"x\"}");
        var harness = KernelHarness.Create(batch, batch, StreamScript.Text("ok"));
        harness.Registry.Register(new FakeTool("echo", _ => ToolResult.Success("x")));

        var result = await harness.Runtime.RunTurnAsync("loop", CancellationToken.None);

        result.DoomLoopDetected.Should().BeFalse();
        harness.Provider.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task ToolPipelineOrderIsDeterministic()
    {
        var harness = KernelHarness.Create(StreamScript.ToolCall("call-1", "echo", "{}"), StreamScript.Text("done"));
        harness.Registry.Register(new FakeTool("echo", _ => ToolResult.Success("ok")));

        await harness.Runtime.RunTurnAsync("order", CancellationToken.None);

        harness.Pipeline.StepLog.Should().Equal(
            "parse", "schema-validate", "sanity-check", "plan-mode-guard", "permission", "cache-lookup",
            "pre-hook", "execute", "post-hook", "artifact-store", "cache-write", "invalidate");
    }

    [Fact]
    public async Task PermissionDenialPreventsToolExecution()
    {
        var harness = KernelHarness.Create(StreamScript.ToolCall("call-1", "danger", "{}"), StreamScript.Text("denied"));
        var tool = new FakeTool("danger", _ => ToolResult.Success("should-not-run"));
        harness.Registry.Register(tool);
        harness.PermissionEngine.DenyTool("danger");

        await harness.Runtime.RunTurnAsync("deny", CancellationToken.None);

        tool.ExecutionCount.Should().Be(0);
        harness.State.Messages.Should().Contain(m => m.Role == MessageRole.Tool && m.ToolCallId == "call-1" && m.Content.Contains("denied", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task JsonlPersistenceUsesHeaderAndMessageLinesAndLoadsHeaderStructurally()
    {
        var state = new SessionState("session-1", "/tmp/work", DateTimeOffset.Parse("2026-05-25T00:00:00Z"));
        state.Messages.Add(new Message(MessageRole.User, "this message mentions session_id but is not a header"));
        var store = new JsonlSessionStore();

        var jsonl = await store.SerializeAsync(state, CancellationToken.None);
        var loaded = await store.DeserializeAsync(jsonl, CancellationToken.None);

        jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(2);
        loaded.SessionId.Should().Be("session-1");
        loaded.Messages.Should().ContainSingle(m => m.Content.Contains("session_id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AtomicPersistenceUsesTempFileFsyncAndRename()
    {
        var fs = new RecordingAtomicFileSystem();
        var store = new JsonlSessionStore(fs);
        var state = new SessionState("session-1", "/tmp/work", DateTimeOffset.Parse("2026-05-25T00:00:00Z"));

        await store.SaveAsync(state, CancellationToken.None);

        fs.Operations.Should().Equal("write-temp", "fsync-temp", "rename");
    }

    [Fact]
    public async Task MixedStreamChunkFieldsAreAllProcessed()
    {
        var harness = KernelHarness.Create(StreamScript.Mixed(thinking: "think", text: "visible", usage: new Usage(1, 2)));

        var result = await harness.Runtime.RunTurnAsync("mixed", CancellationToken.None);

        result.FinalText.Should().Be("visible");
        harness.Events.Thinking.Should().ContainSingle().Which.Should().Be("think");
        harness.Events.Text.Should().ContainSingle().Which.Should().Be("visible");
        harness.Events.Usage.Should().ContainSingle().Which.Should().Be(new Usage(1, 2));
    }
}
