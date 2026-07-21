using System.Text.Json;
using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Tools;

public sealed class PermissionApprovalTests
{
    private static string Json(object value) => JsonSerializer.Serialize(value);

    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "freeagent-tests", Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    private static ToolPipeline Pipeline(TempWorkspace work, IPermissionApprover? approver, out SessionState state)
    {
        var registry = new ToolRegistry();
        registry.Register(new WriteFileTool());
        registry.Register(new ProcessExecTool());
        state = new SessionState("s", work.Root, DateTimeOffset.UnixEpoch);
        return new ToolPipeline(registry, new PermissionEngine(), approver);
    }

    private static ToolCall Write(string name) => new("c1", "WriteFile", Json(new { path = name, content = "x" }));

    // ── engine outcome ──────────────────────────────────────────────────────

    [Fact]
    public void UncoveredCapabilityIsPromptNotHardDeny()
    {
        var engine = new PermissionEngine();
        var tool = new FakeTool("WriteFile", _ => ToolResult.Success("x"));

        var prompt = engine.Decide(tool, [new FileWriteCap("/outside/x")], "/work");
        prompt.Outcome.Should().Be(PermissionOutcome.Prompt);
        prompt.Allowed.Should().BeFalse();

        var hardBlock = engine.Decide(tool, [new FileWriteCap("/etc/passwd")], "/work");
        hardBlock.Outcome.Should().Be(PermissionOutcome.Deny);
    }

    // ── pipeline behavior ─────────────────────────────────────────────────────

    [Fact]
    public async Task NoApproverTreatsPromptAsDenial()
    {
        using var work = new TempWorkspace();
        var pipeline = Pipeline(work, approver: null, out var state);

        var result = await pipeline.ExecuteAsync(Write("out.txt"), state, CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.PermissionDenied);
        File.Exists(Path.Combine(work.Root, "out.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task DenyApproverDeniesAndDoesNotExecute()
    {
        using var work = new TempWorkspace();
        var approver = new FakeApprover(ApprovalDecision.Deny);
        var pipeline = Pipeline(work, approver, out var state);

        var result = await pipeline.ExecuteAsync(Write("out.txt"), state, CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.PermissionDenied);
        approver.CallCount.Should().Be(1);
        File.Exists(Path.Combine(work.Root, "out.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task OnceApproverExecutesButDoesNotRemember()
    {
        using var work = new TempWorkspace();
        var approver = new FakeApprover(ApprovalDecision.Once);
        var pipeline = Pipeline(work, approver, out var state);

        (await pipeline.ExecuteAsync(Write("a.txt"), state, CancellationToken.None)).Kind.Should().Be(ToolResultKind.Success);
        (await pipeline.ExecuteAsync(Write("b.txt"), state, CancellationToken.None)).Kind.Should().Be(ToolResultKind.Success);

        approver.CallCount.Should().Be(2); // asked every time
        state.SessionApprovals.Should().BeEmpty();
    }

    [Fact]
    public async Task SessionApproverExecutesAndRemembersForTheRestOfTheSession()
    {
        using var work = new TempWorkspace();
        var approver = new FakeApprover(ApprovalDecision.Session);
        var pipeline = Pipeline(work, approver, out var state);

        (await pipeline.ExecuteAsync(Write("a.txt"), state, CancellationToken.None)).Kind.Should().Be(ToolResultKind.Success);
        (await pipeline.ExecuteAsync(Write("b.txt"), state, CancellationToken.None)).Kind.Should().Be(ToolResultKind.Success);

        approver.CallCount.Should().Be(1); // second call short-circuited by the session grant
        state.SessionApprovals.Should().Contain("FileWriteCap");
    }

    [Fact]
    public async Task HardBlockedCapabilityIsNeverSentToTheApprover()
    {
        using var work = new TempWorkspace();
        var approver = new FakeApprover(ApprovalDecision.Session); // would approve anything
        var pipeline = Pipeline(work, approver, out var state);

        var result = await pipeline.ExecuteAsync(
            new ToolCall("c1", "ProcessExec", Json(new { command = "sudo", args = new[] { "ls" } })),
            state, CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.PermissionDenied);
        result.Content.Should().ContainEquivalentOf("blocked");
        approver.CallCount.Should().Be(0); // hard deny is not approvable
    }
}
