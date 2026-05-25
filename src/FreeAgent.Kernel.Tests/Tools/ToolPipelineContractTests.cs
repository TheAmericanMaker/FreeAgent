using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests;

public sealed class ToolPipelineContractTests
{
    // ── A. ToolResult taxonomy ───────────────────────────────────────────────

    [Fact]
    public void SuccessIsNotAnError()
    {
        var result = ToolResult.Success("ok");

        result.Kind.Should().Be(ToolResultKind.Success);
        result.IsError.Should().BeFalse();
    }

    [Fact]
    public void InvalidInputIsAnError()
    {
        ToolResult.Error("bad input").IsError.Should().BeTrue();
        ToolResult.Error("bad input").Kind.Should().Be(ToolResultKind.InvalidInput);
        ToolResult.InvalidInput("bad input").Kind.Should().Be(ToolResultKind.InvalidInput);
    }

    [Fact]
    public void PermissionDeniedIsAnErrorAndPreservesRetryHint()
    {
        var result = ToolResult.PermissionDenied("nope", retryHint: "ask the user");

        result.IsError.Should().BeTrue();
        result.Kind.Should().Be(ToolResultKind.PermissionDenied);
        result.RetryHint.Should().Be("ask the user");
    }

    [Fact]
    public void StateConflictIsAnErrorAndPreservesRetryHint()
    {
        var result = ToolResult.StateConflict("stale", retryHint: "re-read then retry");

        result.IsError.Should().BeTrue();
        result.Kind.Should().Be(ToolResultKind.StateConflict);
        result.RetryHint.Should().Be("re-read then retry");
    }

    [Fact]
    public void CrashIsAnErrorAndPreservesRetryHint()
    {
        var result = ToolResult.Crash("boom", retryHint: "try again");

        result.IsError.Should().BeTrue();
        result.Kind.Should().Be(ToolResultKind.Crash);
        result.RetryHint.Should().Be("try again");
    }

    [Fact]
    public void EmptyIsAnError()
    {
        var result = ToolResult.Empty("no output");

        result.IsError.Should().BeTrue();
        result.Kind.Should().Be(ToolResultKind.Empty);
    }

    [Fact]
    public void CancelledIsAnError()
    {
        var result = ToolResult.Cancelled();

        result.IsError.Should().BeTrue();
        result.Kind.Should().Be(ToolResultKind.Cancelled);
        result.Content.Should().NotBeNullOrWhiteSpace();
    }

    // ── Pipeline short-circuit behavior ──────────────────────────────────────

    private static SessionState NewState() =>
        new("contract-session", "/tmp/freeagent", DateTimeOffset.Parse("2026-05-25T00:00:00Z"));

    private static ToolCall Call(string name, string argumentsJson) => new("call-1", name, argumentsJson);

    // B. Invalid JSON is returned as InvalidInput, not thrown.
    [Fact]
    public async Task InvalidJsonArgumentsReturnInvalidInputAndDoNotExecuteTheTool()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool("echo", _ => ToolResult.Success("should-not-run"));
        registry.Register(tool);
        var perms = RecordingPermissionEngine.Allowing();
        var pipeline = new ToolPipeline(registry, perms);

        var result = await pipeline.ExecuteAsync(Call("echo", "{not valid json"), NewState(), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
        result.Content.Should().ContainEquivalentOf("invalid json");
        tool.ExecutionCount.Should().Be(0);
        perms.DecideCallCount.Should().Be(0);
        pipeline.StepLog.Should().Equal("parse");
    }

    // C. Unknown tool short-circuits before the permission step.
    [Fact]
    public async Task UnknownToolReturnsInvalidInputBeforePermissionCheck()
    {
        var registry = new ToolRegistry();
        var perms = RecordingPermissionEngine.Allowing();
        var pipeline = new ToolPipeline(registry, perms);

        var result = await pipeline.ExecuteAsync(Call("ghost", "{}"), NewState(), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
        result.Content.Should().Contain("ghost");
        perms.DecideCallCount.Should().Be(0);
        pipeline.StepLog.Should().NotContain("execute");
        pipeline.StepLog.Should().NotContain("permission");
    }

    // D. Permission denial has its own result class and stops before execute.
    [Fact]
    public async Task PermissionDenialReturnsPermissionDeniedAndDoesNotExecute()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool("danger", _ => ToolResult.Success("should-not-run"));
        registry.Register(tool);
        var perms = RecordingPermissionEngine.Denying("danger denied");
        var pipeline = new ToolPipeline(registry, perms);

        var result = await pipeline.ExecuteAsync(Call("danger", "{}"), NewState(), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.PermissionDenied);
        result.Content.Should().ContainEquivalentOf("denied");
        perms.DecideCallCount.Should().Be(1);
        tool.ExecutionCount.Should().Be(0);
        pipeline.StepLog.Should().Contain("permission");
        pipeline.StepLog.Should().NotContain("execute");
    }

    // E. OperationCanceledException maps to Cancelled and stops after execute.
    [Fact]
    public async Task OperationCanceledExceptionMapsToCancelled()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool("slow", _ => throw new OperationCanceledException());
        registry.Register(tool);
        var pipeline = new ToolPipeline(registry, RecordingPermissionEngine.Allowing());

        var result = await pipeline.ExecuteAsync(Call("slow", "{}"), NewState(), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Cancelled);
        tool.ExecutionCount.Should().Be(1);
        pipeline.StepLog.Should().Contain("execute");
        pipeline.StepLog.Should().NotContain("post-hook");
        pipeline.StepLog.Should().NotContain("artifact-store");
    }

    // F. Unexpected tool exception maps to Crash with a retry hint and no stack trace.
    [Fact]
    public async Task UnexpectedToolExceptionMapsToCrashWithRetryHint()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool("boomer", _ => throw new InvalidOperationException("boom"));
        registry.Register(tool);
        var pipeline = new ToolPipeline(registry, RecordingPermissionEngine.Allowing());

        var result = await pipeline.ExecuteAsync(Call("boomer", "{}"), NewState(), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Crash);
        result.Content.Should().ContainEquivalentOf("crash");
        result.Content.Should().NotContain("   at "); // no raw stack-trace frames leaked to the model
        result.RetryHint.Should().NotBeNullOrWhiteSpace();
        pipeline.StepLog.Should().Contain("execute");
        pipeline.StepLog.Should().NotContain("post-hook");
    }

    // G. A successful tool with empty output is converted to Empty.
    [Fact]
    public async Task EmptySuccessfulOutputIsConvertedToEmpty()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool("quiet", _ => ToolResult.Success("   "));
        registry.Register(tool);
        var pipeline = new ToolPipeline(registry, RecordingPermissionEngine.Allowing());

        var result = await pipeline.ExecuteAsync(Call("quiet", "{}"), NewState(), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Empty);
    }

    // H. The successful path records all 12 conceptual steps in strict order.
    [Fact]
    public async Task SuccessfulPathRecordsTwelveStepsInOrder()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool("echo", _ => ToolResult.Success("ok")));
        var pipeline = new ToolPipeline(registry, RecordingPermissionEngine.Allowing());

        var result = await pipeline.ExecuteAsync(Call("echo", "{}"), NewState(), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Be("ok");
        pipeline.StepLog.Should().Equal(
            "parse", "schema-validate", "sanity-check", "plan-mode-guard", "permission", "cache-lookup",
            "pre-hook", "execute", "post-hook", "artifact-store", "cache-write", "invalidate");
    }
}
