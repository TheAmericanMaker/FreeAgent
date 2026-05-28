using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Sessions;

public sealed class CompactorTests
{
    private static Message U(string text) => new(MessageRole.User, text);
    private static Message A(string text) => new(MessageRole.Assistant, text);
    private static Message A(string text, IReadOnlyList<ToolCall> calls) => new(MessageRole.Assistant, text, calls);
    private static Message T(string id, string content) => new(MessageRole.Tool, content, ToolCallId: id, ToolName: "x");
    private static Message Sys(string text) => new(MessageRole.System, text);

    // ── ShouldCompact ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0,    100_000, false)]   // empty
    [InlineData(80_000,  100_000, false)] // boundary: > threshold, not >=
    [InlineData(80_001,  100_000, true)]  // just over
    [InlineData(1_000_000, 0, false)]     // unknown context window → never compacts
    public void ShouldCompactRespectsThresholdAndContextWindow(int lastInput, int contextWindow, bool expected)
    {
        Compactor.ShouldCompact(lastInput, contextWindow).Should().Be(expected);
    }

    // ── Compact ─────────────────────────────────────────────────────────────

    [Fact]
    public void CompactReturnsCopyUnchangedWhenAtOrBelowKeepLimit()
    {
        var messages = new List<Message> { Sys("you are"), U("hi"), A("hello") };

        var result = Compactor.Compact(messages, keepLastTurns: 4);

        result.Should().Equal(messages);
    }

    [Fact]
    public void CompactPreservesSystemBlockAndKeepsLastKTurns()
    {
        var messages = new List<Message>
        {
            Sys("you are"),
            U("turn 1"), A("reply 1"),
            U("turn 2"), A("reply 2"),
            U("turn 3"), A("reply 3"),
            U("turn 4"), A("reply 4"),
            U("turn 5"), A("reply 5"),
            U("turn 6"), A("reply 6"),
        };

        var result = Compactor.Compact(messages, keepLastTurns: 2);

        result[0].Role.Should().Be(MessageRole.System);
        result.Count(m => m.Role == MessageRole.User).Should().Be(2); // last 2 turns
        result[1].Role.Should().Be(MessageRole.User);
        result[1].Content.Should().StartWith("[Compacted:");
        result[1].Content.Should().Contain("turn 5"); // notice + original content
        // The last assistant/user pair is preserved verbatim.
        result[^2].Should().BeEquivalentTo(U("turn 6"));
        result[^1].Should().BeEquivalentTo(A("reply 6"));
    }

    [Fact]
    public void CompactKeepsToolUseAndToolResultPairingsTogether()
    {
        // Each turn is a User followed by an Assistant (with a tool_call) followed by a Tool
        // result. Compaction by turn boundary should never split a tool_use / tool_result pair.
        var messages = new List<Message>
        {
            U("t1"), A("calling", [new ToolCall("c1", "x", "{}")]), T("c1", "ok"),
            U("t2"), A("calling", [new ToolCall("c2", "x", "{}")]), T("c2", "ok"),
            U("t3"), A("calling", [new ToolCall("c3", "x", "{}")]), T("c3", "ok"),
        };

        var result = Compactor.Compact(messages, keepLastTurns: 1);

        result.Should().HaveCount(3);
        result[0].Role.Should().Be(MessageRole.User);          // the notice-prefixed user
        result[0].Content.Should().StartWith("[Compacted:");
        result[0].Content.Should().Contain("t3");
        result[1].Role.Should().Be(MessageRole.Assistant);
        result[1].ToolCalls.Should().ContainSingle().Which.Id.Should().Be("c3");
        result[2].Role.Should().Be(MessageRole.Tool);
        result[2].ToolCallId.Should().Be("c3");
    }

    [Fact]
    public void CompactWithNoSystemBlockStillStartsKeptBlockOnUser()
    {
        var messages = new List<Message>
        {
            U("t1"), A("r1"),
            U("t2"), A("r2"),
            U("t3"), A("r3"),
        };

        var result = Compactor.Compact(messages, keepLastTurns: 1);

        result[0].Role.Should().Be(MessageRole.User);
        result[0].Content.Should().StartWith("[Compacted:");
        result.Count(m => m.Role == MessageRole.User).Should().Be(1);
    }
}
