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
    public void PlanModeBlockedIsAnErrorWithTheMandatedMessageAndRecoveryHint()
    {
        var result = ToolResult.PlanModeBlocked("FileWrite");

        result.IsError.Should().BeTrue();
        result.Kind.Should().Be(ToolResultKind.PlanModeBlocked);
        result.Content.Should().Be(
            "Plan mode is active — only read-only tools are allowed. Call ExitPlanMode first to make changes with FileWrite.");
        result.RetryHint.Should().Be("Call ExitPlanMode first to make changes with FileWrite.");
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

    // ── Plan-mode guard (pipeline step 4) ────────────────────────────────────

    private static SessionState PlanModeState()
    {
        var state = NewState();
        state.PlanMode = true;
        return state;
    }

    // L. In plan mode a non-read-only tool is blocked at step 4, before permission and execute.
    [Fact]
    public async Task PlanModeBlocksNonReadOnlyToolBeforePermissionAndExecute()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool("writer", _ => ToolResult.Success("should-not-run"), isReadOnly: false);
        registry.Register(tool);
        var perms = RecordingPermissionEngine.Allowing();
        var pipeline = new ToolPipeline(registry, perms);

        var result = await pipeline.ExecuteAsync(Call("writer", "{}"), PlanModeState(), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.PlanModeBlocked);
        result.IsError.Should().BeTrue();
        tool.ExecutionCount.Should().Be(0);
        perms.DecideCallCount.Should().Be(0);
        pipeline.StepLog.Should().Equal("parse", "schema-validate", "sanity-check", "plan-mode-guard");
        pipeline.StepLog.Should().NotContain("permission");
        pipeline.StepLog.Should().NotContain("execute");
    }

    // M. The blocked result carries the exact mandated message, naming the offending tool.
    [Fact]
    public async Task PlanModeBlockReturnsTheExactMandatedMessage()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool("FileWrite", _ => ToolResult.Success("should-not-run"), isReadOnly: false));
        var pipeline = new ToolPipeline(registry, RecordingPermissionEngine.Allowing());

        var result = await pipeline.ExecuteAsync(Call("FileWrite", "{}"), PlanModeState(), CancellationToken.None);

        result.Content.Should().Be(
            "Plan mode is active — only read-only tools are allowed. Call ExitPlanMode first to make changes with FileWrite.");
    }

    // N. In plan mode a read-only tool is unaffected and runs all 12 steps.
    [Fact]
    public async Task PlanModeAllowsReadOnlyToolToExecuteAllTwelveSteps()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool("reader", _ => ToolResult.Success("data"), isReadOnly: true);
        registry.Register(tool);
        var pipeline = new ToolPipeline(registry, RecordingPermissionEngine.Allowing());

        var result = await pipeline.ExecuteAsync(Call("reader", "{}"), PlanModeState(), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Be("data");
        tool.ExecutionCount.Should().Be(1);
        pipeline.StepLog.Should().Equal(
            "parse", "schema-validate", "sanity-check", "plan-mode-guard", "permission", "cache-lookup",
            "pre-hook", "execute", "post-hook", "artifact-store", "cache-write", "invalidate");
    }

    // O. With plan mode off (the default) a non-read-only tool is not blocked by the guard.
    [Fact]
    public async Task PlanModeOffDoesNotBlockNonReadOnlyTool()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool("writer", _ => ToolResult.Success("wrote"), isReadOnly: false);
        registry.Register(tool);
        var pipeline = new ToolPipeline(registry, RecordingPermissionEngine.Allowing());

        var result = await pipeline.ExecuteAsync(Call("writer", "{}"), NewState(), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        tool.ExecutionCount.Should().Be(1);
        pipeline.StepLog.Should().Contain("execute");
    }

    // ── Schema validation (pipeline step 2) ──────────────────────────────────

    private const string PathSchema =
        """{"type":"object","required":["path"],"properties":{"path":{"type":"string"}}}""";

    // A. A required-property failure short-circuits at schema-validate.
    [Fact]
    public async Task SchemaValidationFailureReturnsInvalidInputAndShortCircuitsBeforePermission()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool("reader", _ => ToolResult.Success("should-not-run"), schemaJson: PathSchema);
        registry.Register(tool);
        var perms = RecordingPermissionEngine.Allowing();
        var pipeline = new ToolPipeline(registry, perms);

        var result = await pipeline.ExecuteAsync(Call("reader", "{}"), NewState(), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
        result.Content.Should().Contain("path");
        result.Content.Should().ContainEquivalentOf("required");
        perms.DecideCallCount.Should().Be(0);
        tool.ExecutionCount.Should().Be(0);
        pipeline.StepLog.Should().Contain("schema-validate");
        pipeline.StepLog.Should().NotContain("permission");
        pipeline.StepLog.Should().NotContain("execute");
    }

    // B (pipeline). Wrong primitive type also fails through the pipeline.
    [Fact]
    public async Task SchemaTypeMismatchReturnsInvalidInput()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool(
            "counter",
            _ => ToolResult.Success("should-not-run"),
            schemaJson: """{"type":"object","properties":{"count":{"type":"integer"}}}""");
        registry.Register(tool);
        var pipeline = new ToolPipeline(registry, RecordingPermissionEngine.Allowing());

        var result = await pipeline.ExecuteAsync(Call("counter", """{"count":"nope"}"""), NewState(), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
        result.Content.Should().Contain("count");
        tool.ExecutionCount.Should().Be(0);
    }

    // I (pipeline). A malformed tool schema is reported as InvalidInput, not a crash.
    [Fact]
    public async Task MalformedToolSchemaReturnsInvalidInputWithoutCrashing()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool(
            "broken",
            _ => ToolResult.Success("should-not-run"),
            schemaJson: """{"type":"object","required":"path"}""");
        registry.Register(tool);
        var pipeline = new ToolPipeline(registry, RecordingPermissionEngine.Allowing());

        var result = await pipeline.ExecuteAsync(Call("broken", "{}"), NewState(), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
        tool.ExecutionCount.Should().Be(0);
    }

    // K. Schema failure stops before RequiredCapabilities is consulted.
    [Fact]
    public async Task SchemaValidationFailureDoesNotCallRequiredCapabilities()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool(
            "reader",
            _ => ToolResult.Success("should-not-run"),
            capabilities: (_, _) => throw new InvalidOperationException("capabilities must not be collected on schema failure"),
            schemaJson: PathSchema);
        registry.Register(tool);
        var pipeline = new ToolPipeline(registry, RecordingPermissionEngine.Allowing());

        var result = await pipeline.ExecuteAsync(Call("reader", "{}"), NewState(), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
        tool.ExecutionCount.Should().Be(0);
    }

    // J. Valid arguments proceed through permission/capabilities to execution.
    [Fact]
    public async Task ValidArgumentsProceedToExecutionAndRecordTwelveSteps()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool(
            "reader",
            _ => ToolResult.Success("data"),
            isReadOnly: true,
            capabilities: (_, ctx) => [new FileReadCap(Path.Combine(ctx.Session.WorkingDirectory, "a.txt"))],
            schemaJson: PathSchema);
        registry.Register(tool);
        var pipeline = new ToolPipeline(registry, new PermissionEngine());

        var result = await pipeline.ExecuteAsync(Call("reader", """{"path":"a.txt"}"""), NewState(), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Be("data");
        pipeline.StepLog.Should().Equal(
            "parse", "schema-validate", "sanity-check", "plan-mode-guard", "permission", "cache-lookup",
            "pre-hook", "execute", "post-hook", "artifact-store", "cache-write", "invalidate");
    }
}
