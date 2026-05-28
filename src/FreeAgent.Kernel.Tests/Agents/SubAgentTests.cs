using System.Text.Json;
using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Agents;

public sealed class SubAgentTests
{
    private static SessionState ParentState() => new("p", "/tmp/work", DateTimeOffset.UnixEpoch);

    private static AgentRegistry NewRegistry()
    {
        var r = new AgentRegistry();
        r.Register(new AgentDefinition("Explore", ["ReadFile", "Glob"], "You explore."));
        r.Register(new AgentDefinition("Coder", ["ReadFile", "WriteFile"], "You code."));
        return r;
    }

    // ── AgentRegistry ─────────────────────────────────────────────────────────

    [Fact]
    public void RegistryStoresAndRetrievesAgents()
    {
        var registry = NewRegistry();

        registry.Types.Should().BeEquivalentTo("Explore", "Coder");
        registry.Find("Explore").Should().NotBeNull();
        registry.Find("Missing").Should().BeNull();
    }

    // ── SubAgentRunner ────────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownAgentTypeThrowsArgumentException()
    {
        var parentTools = new ToolRegistry();
        var provider = new FakeProvider([StreamScript.Text("hi")]);
        var runner = new SubAgentRunner(provider, parentTools, new PermissionEngine(), NewRegistry());

        var act = async () => await runner.RunAsync("Unknown", "task", ParentState(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Explore*Coder*");
    }

    [Fact]
    public async Task SubAgentRunReturnsFinalTextFromProvider()
    {
        var parentTools = new ToolRegistry();
        var provider = new FakeProvider([StreamScript.Text("sub-agent reply")]);
        var runner = new SubAgentRunner(provider, parentTools, new PermissionEngine(), NewRegistry());

        var text = await runner.RunAsync("Explore", "investigate", ParentState(), CancellationToken.None);

        text.Should().Be("sub-agent reply");
    }

    [Fact]
    public async Task SystemPromptSuffixIsSeededAsTheSubSessionsFirstMessage()
    {
        var parentTools = new ToolRegistry();
        var provider = new FakeProvider([StreamScript.Text("done")]);
        var runner = new SubAgentRunner(provider, parentTools, new PermissionEngine(), NewRegistry());

        await runner.RunAsync("Explore", "anything", ParentState(), CancellationToken.None);

        // The FakeProvider records the request it received; the first message should be the
        // role-specific system prompt suffix.
        provider.Requests.Should().HaveCount(1);
        provider.Requests[0].Messages[0].Role.Should().Be(MessageRole.System);
        provider.Requests[0].Messages[0].Content.Should().Be("You explore.");
    }

    // ── SpawnAgentTool ────────────────────────────────────────────────────────

    [Fact]
    public void CapabilityIsAgentSpawnCapWithTypeAndTask()
    {
        var parentTools = new ToolRegistry();
        var provider = new FakeProvider([StreamScript.Text("x")]);
        var runner = new SubAgentRunner(provider, parentTools, new PermissionEngine(), NewRegistry());
        var tool = new SpawnAgentTool(runner, NewRegistry());

        var caps = tool.RequiredCapabilities(
            JsonDocument.Parse("""{"type":"Explore","task":"look around"}"""),
            new ToolContext(ParentState()));

        var cap = caps.Should().ContainSingle().Which.Should().BeOfType<AgentSpawnCap>().Subject;
        cap.AgentType.Should().Be("Explore");
        cap.TaskSummary.Should().Be("look around");
    }

    [Fact]
    public async Task InvalidTypeReturnsInvalidInputResult()
    {
        var parentTools = new ToolRegistry();
        var provider = new FakeProvider([StreamScript.Text("x")]);
        var runner = new SubAgentRunner(provider, parentTools, new PermissionEngine(), NewRegistry());
        var tool = new SpawnAgentTool(runner, NewRegistry());

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("""{"type":"Nope","task":"x"}"""),
            new ToolContext(ParentState()),
            CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
    }

    [Fact]
    public async Task EmptyTaskReturnsInvalidInput()
    {
        var parentTools = new ToolRegistry();
        var provider = new FakeProvider([StreamScript.Text("x")]);
        var runner = new SubAgentRunner(provider, parentTools, new PermissionEngine(), NewRegistry());
        var tool = new SpawnAgentTool(runner, NewRegistry());

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("""{"type":"Explore","task":""}"""),
            new ToolContext(ParentState()),
            CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
    }

    [Fact]
    public async Task SubAgentReceivesOnlyTheRolesAllowedTools()
    {
        // The sub-agent's "registry" is filtered to AllowedTools. We assert by inspecting the tools
        // sent in the provider request: only the role's allow-list appears.
        var parentTools = new ToolRegistry();
        parentTools.Register(new FakeTool("ReadFile", _ => ToolResult.Success("ok"), isReadOnly: true));
        parentTools.Register(new FakeTool("WriteFile", _ => ToolResult.Success("ok")));
        parentTools.Register(new FakeTool("ProcessExec", _ => ToolResult.Success("ok")));
        parentTools.Register(new FakeTool("Glob", _ => ToolResult.Success("ok"), isReadOnly: true));
        var provider = new FakeProvider([StreamScript.Text("done")]);
        var runner = new SubAgentRunner(provider, parentTools, new PermissionEngine(), NewRegistry());

        await runner.RunAsync("Explore", "task", ParentState(), CancellationToken.None);

        var toolsInRequest = provider.Requests[0].Tools.Select(t => t.Name).ToHashSet();
        toolsInRequest.Should().BeEquivalentTo("ReadFile", "Glob"); // Explore's allow-list
        toolsInRequest.Should().NotContain("WriteFile").And.NotContain("ProcessExec");
    }
}
