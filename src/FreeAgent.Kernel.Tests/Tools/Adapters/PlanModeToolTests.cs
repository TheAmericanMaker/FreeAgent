using System.Text.Json;
using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Tools.Adapters;

public sealed class PlanModeToolTests
{
    private static JsonDocument Empty() => JsonDocument.Parse("{}");
    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static (ToolContext Context, SessionState State) NewContext()
    {
        var state = new SessionState("plan-session", "/tmp/work", DateTimeOffset.UnixEpoch);
        return (new ToolContext(state), state);
    }

    [Fact]
    public void PlanModeToolsAreReadOnlyButNotConcurrencySafe()
    {
        var enter = new EnterPlanModeTool();
        var exit = new ExitPlanModeTool();

        enter.IsReadOnly.Should().BeTrue();
        enter.IsConcurrencySafe.Should().BeFalse();
        exit.IsReadOnly.Should().BeTrue();
        exit.IsConcurrencySafe.Should().BeFalse();
        enter.RequiredCapabilities(Empty(), NewContext().Context).Should().BeEmpty();
        exit.RequiredCapabilities(Empty(), NewContext().Context).Should().BeEmpty();
    }

    [Fact]
    public async Task EnterAndExitTogglePlanModeOnTheSession()
    {
        var (context, state) = NewContext();
        state.PlanMode.Should().BeFalse();

        await new EnterPlanModeTool().ExecuteAsync(Empty(), context, CancellationToken.None);
        state.PlanMode.Should().BeTrue();

        await new ExitPlanModeTool().ExecuteAsync(Empty(), context, CancellationToken.None);
        state.PlanMode.Should().BeFalse();
    }

    [Fact]
    public async Task WhileInPlanModeWritesAreBlockedButExitPlanModeStillRuns()
    {
        var registry = new ToolRegistry();
        registry.Register(new WriteFileTool());
        registry.Register(new ExitPlanModeTool());
        var engine = new PermissionEngine();
        engine.AllowCapabilityType<FileWriteCap>(); // even with writes allowed, plan mode blocks them
        var pipeline = new ToolPipeline(registry, engine);
        var state = new SessionState("s", "/tmp/work", DateTimeOffset.UnixEpoch) { PlanMode = true };

        // A writable tool is blocked at the plan-mode guard, before permission.
        var write = await pipeline.ExecuteAsync(
            new ToolCall("c1", "WriteFile", Json(new { path = "out.txt", content = "x" })), state, CancellationToken.None);
        write.Kind.Should().Be(ToolResultKind.PlanModeBlocked);

        // ExitPlanMode is read-only, so it runs even while plan mode is active, and clears the flag.
        var exit = await pipeline.ExecuteAsync(new ToolCall("c2", "ExitPlanMode", "{}"), state, CancellationToken.None);
        exit.Kind.Should().Be(ToolResultKind.Success);
        state.PlanMode.Should().BeFalse();
    }
}
