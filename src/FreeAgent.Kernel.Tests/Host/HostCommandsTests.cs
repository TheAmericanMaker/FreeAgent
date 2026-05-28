using FluentAssertions;
using FreeAgent.Host;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Host;

public sealed class HostCommandsTests
{
    private static SessionState State() => new("abcd1234", "/home/u/proj", DateTimeOffset.UnixEpoch);

    [Fact]
    public void HelpListsTheCommands()
    {
        var help = HostCommands.HelpText();
        help.Should().Contain("/help").And.Contain("/status").And.Contain("/model").And.Contain("/plan");
    }

    [Fact]
    public void StatusReportsSessionModelDirAndCounts()
    {
        var state = State();
        state.Messages.Add(new Message(MessageRole.System, "sys"));
        state.SessionApprovals.Add("FileWriteCap");

        var status = HostCommands.StatusText(state, "gpt-4o-mini");

        status.Should().Contain("abcd1234").And.Contain("gpt-4o-mini").And.Contain("/home/u/proj");
        status.Should().Contain("Messages:").And.Contain("1");
        status.Should().Contain("FileWriteCap");
    }

    [Fact]
    public void ModelShowsTheActiveModel()
    {
        HostCommands.ModelText("qwen2.5-coder").Should().Contain("qwen2.5-coder").And.Contain("FREEMODEL");
    }

    [Fact]
    public void PlanToggleFlipsAndExplicitOnOffSets()
    {
        var state = State();
        state.PlanMode.Should().BeFalse();

        HostCommands.ApplyPlan(state, ["/plan"]);
        state.PlanMode.Should().BeTrue(); // toggled on

        HostCommands.ApplyPlan(state, ["/plan", "off"]);
        state.PlanMode.Should().BeFalse();

        HostCommands.ApplyPlan(state, ["/plan", "on"]);
        state.PlanMode.Should().BeTrue();
    }
}
