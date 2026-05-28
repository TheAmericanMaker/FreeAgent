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
    public void DoctorReportsProviderToolsAgentsAndState()
    {
        var state = State();
        state.PlanMode = true;
        state.SessionApprovals.Add("FileWriteCap");
        var diag = new HostCommands.Diagnostics(
            ProviderName: "anthropic",
            Model: "claude-3-7-sonnet-latest",
            BaseUrl: "https://api.anthropic.com",
            ConfigPath: "/home/u/.config/freeagent/config.json",
            ToolNames: ["ReadFile", "WriteFile", "EditFile"],
            AgentTypes: ["Explore", "Plan"]);

        var doc = HostCommands.DoctorText(state, diag);

        doc.Should().Contain("anthropic").And.Contain("claude-3-7-sonnet-latest");
        doc.Should().Contain("https://api.anthropic.com").And.Contain("/home/u/.config/freeagent/config.json");
        doc.Should().Contain("ReadFile, WriteFile, EditFile");
        doc.Should().Contain("Explore, Plan");
        doc.Should().Contain("Plan mode:  ON");
        doc.Should().Contain("FileWriteCap");
    }

    [Fact]
    public void RevertDropsTheRequestedNumberOfTurnsPreservingSystemMessages()
    {
        var state = State();
        state.Messages.Add(new Message(MessageRole.System, "sys"));
        state.Messages.Add(new Message(MessageRole.User, "turn 1"));
        state.Messages.Add(new Message(MessageRole.Assistant, "reply 1"));
        state.Messages.Add(new Message(MessageRole.User, "turn 2"));
        state.Messages.Add(new Message(MessageRole.Assistant, "reply 2"));
        state.Messages.Add(new Message(MessageRole.User, "turn 3"));
        state.Messages.Add(new Message(MessageRole.Assistant, "reply 3"));

        HostCommands.Revert(state, ["/revert"]).Should().Contain("Reverted 1");
        state.Messages.Select(m => m.Content).Should().Equal("sys", "turn 1", "reply 1", "turn 2", "reply 2");

        HostCommands.Revert(state, ["/revert", "2"]).Should().Contain("Reverted 2");
        state.Messages.Select(m => m.Content).Should().Equal("sys");
    }

    [Fact]
    public void RevertWithNothingToDropReportsNothingToRevert()
    {
        var state = State();
        HostCommands.Revert(state, ["/revert"]).Should().Contain("Nothing to revert");
    }

    [Fact]
    public void RevertBeyondAvailableTurnsRefuses()
    {
        var state = State();
        state.Messages.Add(new Message(MessageRole.User, "only"));
        HostCommands.Revert(state, ["/revert", "5"]).Should().Contain("Only 1");
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
