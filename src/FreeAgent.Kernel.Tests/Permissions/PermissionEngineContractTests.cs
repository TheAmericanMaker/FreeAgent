using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests;

public sealed class PermissionEngineContractTests
{
    private const string WorkDir = "/tmp/work";
    private static readonly DateTimeOffset When = DateTimeOffset.Parse("2026-05-25T00:00:00Z");

    private static ITool Tool(string name = "tool") => new FakeTool(name, _ => ToolResult.Success("ok"));

    // ── A. Empty capabilities allow by default ───────────────────────────────

    [Fact]
    public void EmptyCapabilitiesAreAllowed()
    {
        var engine = new PermissionEngine();

        engine.Decide(Tool("t"), [], WorkDir).Allowed.Should().BeTrue();
    }

    [Fact]
    public void DenyToolStillDeniesAToolWithNoCapabilities()
    {
        var engine = new PermissionEngine();
        engine.DenyTool("t");

        var decision = engine.Decide(Tool("t"), [], WorkDir);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().ContainEquivalentOf("denied");
    }

    // ── B. Tool deny beats tool allow ────────────────────────────────────────

    [Fact]
    public void ToolDenyBeatsToolAllow()
    {
        var engine = new PermissionEngine();
        engine.DenyTool("x");
        engine.AllowTool("x");

        engine.Decide(Tool("x"), [], WorkDir).Allowed.Should().BeFalse();
    }

    // ── C. FileReadCap inside working directory auto-allows ──────────────────

    [Fact]
    public void FileReadInsideWorkingDirectoryIsAllowed()
    {
        var engine = new PermissionEngine();

        engine.Decide(Tool(), [new FileReadCap("/tmp/work/src/a.txt")], WorkDir).Allowed.Should().BeTrue();
        engine.Decide(Tool(), [new FileReadCap("src/a.txt")], WorkDir).Allowed.Should().BeTrue();
    }

    [Fact]
    public void FileReadCannotEscapeWorkingDirectoryWithDotDot()
    {
        var engine = new PermissionEngine();

        engine.Decide(Tool(), [new FileReadCap("/tmp/work/../etc/passwd")], WorkDir).Allowed.Should().BeFalse();
        engine.Decide(Tool(), [new FileReadCap("../secrets.txt")], WorkDir).Allowed.Should().BeFalse();
    }

    // ── D. FileReadCap outside working directory denies ──────────────────────

    [Fact]
    public void FileReadOutsideWorkingDirectoryIsDenied()
    {
        var engine = new PermissionEngine();

        var decision = engine.Decide(Tool(), [new FileReadCap("/etc/passwd")], WorkDir);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().ContainEquivalentOf("approval");
    }

    // ── E. FileWriteCap protected paths deny ─────────────────────────────────

    [Theory]
    [InlineData("/etc/hosts")]
    [InlineData("/usr/bin/thing")]
    [InlineData("/bin/sh")]
    [InlineData("/sbin/init")]
    [InlineData("/System/x")]
    [InlineData("/Library/x")]
    public void FileWriteToProtectedPathIsDenied(string path)
    {
        var engine = new PermissionEngine();

        var decision = engine.Decide(Tool(), [new FileWriteCap(path)], WorkDir);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().ContainEquivalentOf("protected");
    }

    // ── F. FileWriteCap ordinary workspace write denies by default ───────────

    [Fact]
    public void FileWriteToWorkspaceRequiresApprovalAndIsDeniedNonInteractively()
    {
        var engine = new PermissionEngine();

        var decision = engine.Decide(Tool(), [new FileWriteCap("/tmp/work/out.txt")], WorkDir);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().ContainEquivalentOf("approval");
    }

    // ── G. Memory read auto-allows; memory write denies by default ───────────

    [Fact]
    public void MemoryReadIsAutoAllowedAndWriteIsDenied()
    {
        var engine = new PermissionEngine();

        engine.Decide(Tool(), [new MemoryCap("notes", "read")], WorkDir).Allowed.Should().BeTrue();
        engine.Decide(Tool(), [new MemoryCap("notes", "write")], WorkDir).Allowed.Should().BeFalse();
    }

    // ── H. Safe ProcessExecCap auto-allows ───────────────────────────────────

    [Fact]
    public void SafeReadOnlyBinariesAreAutoAllowed()
    {
        var engine = new PermissionEngine();

        engine.Decide(Tool(), [new ProcessExecCap("pwd", [])], WorkDir).Allowed.Should().BeTrue();
        engine.Decide(Tool(), [new ProcessExecCap("ls", ["-la"])], WorkDir).Allowed.Should().BeTrue();
        engine.Decide(Tool(), [new ProcessExecCap("git", ["status"])], WorkDir).Allowed.Should().BeTrue();
        engine.Decide(Tool(), [new ProcessExecCap("git", ["diff", "--cached"])], WorkDir).Allowed.Should().BeTrue();
        engine.Decide(Tool(), [new ProcessExecCap("git", ["log"])], WorkDir).Allowed.Should().BeTrue();
    }

    [Fact]
    public void UnsafeGitSubcommandIsNotAutoAllowed()
    {
        var engine = new PermissionEngine();

        engine.Decide(Tool(), [new ProcessExecCap("git", ["push"])], WorkDir).Allowed.Should().BeFalse();
    }

    [Fact]
    public void ReadOnlyFindIsAutoAllowed()
    {
        var engine = new PermissionEngine();

        engine.Decide(Tool(), [new ProcessExecCap("find", [".", "-name", "*.cs"])], WorkDir).Allowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("-delete")]
    [InlineData("-exec")]
    [InlineData("-execdir")]
    [InlineData("-ok")]
    public void DestructiveFindIsNotAutoAllowed(string action)
    {
        var engine = new PermissionEngine();

        var decision = engine.Decide(Tool(), [new ProcessExecCap("find", [".", action, "rm", "{}", ";"])], WorkDir);

        decision.Allowed.Should().BeFalse();
        decision.Outcome.Should().Be(PermissionOutcome.Prompt);
    }

    // ── I. Dangerous ProcessExecCap always denies ────────────────────────────

    [Theory]
    [InlineData("sudo")]
    [InlineData("su")]
    [InlineData("doas")]
    [InlineData("pkexec")]
    [InlineData("chmod")]
    [InlineData("chown")]
    [InlineData("chattr")]
    [InlineData("setfacl")]
    public void BlockedBinariesAreAlwaysDenied(string binary)
    {
        var engine = new PermissionEngine();

        var decision = engine.Decide(Tool(), [new ProcessExecCap(binary, [])], WorkDir);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().ContainEquivalentOf("blocked");
    }

    // ── J. Network / VCS / agent-spawn caps deny by default ──────────────────

    [Fact]
    public void NetworkVcsAndAgentSpawnCapabilitiesDenyByDefault()
    {
        var engine = new PermissionEngine();

        engine.Decide(Tool(), [new NetworkEgressCap("example.com")], WorkDir).Allowed.Should().BeFalse();
        engine.Decide(Tool(), [new VcsMutationCap("origin", "push")], WorkDir).Allowed.Should().BeFalse();
        engine.Decide(Tool(), [new AgentSpawnCap("Explore", "look around")], WorkDir).Allowed.Should().BeFalse();
    }

    // ── Evaluation-order extras (tool/type/rule overrides) ───────────────────

    [Fact]
    public void AllowToolCoversAllCapabilities()
    {
        var engine = new PermissionEngine();
        engine.AllowTool("writer");

        engine.Decide(Tool("writer"), [new FileWriteCap("/tmp/work/out.txt")], WorkDir).Allowed.Should().BeTrue();
    }

    [Fact]
    public void AllowToolDoesNotOverrideBlockedBinary()
    {
        var engine = new PermissionEngine();
        engine.AllowTool("runner");

        engine.Decide(Tool("runner"), [new ProcessExecCap("sudo", ["rm", "-rf", "/"])], WorkDir).Allowed.Should().BeFalse();
    }

    [Fact]
    public void AllowCapabilityTypeCoversThatCapability()
    {
        var engine = new PermissionEngine();
        engine.AllowCapabilityType<FileWriteCap>();

        engine.Decide(Tool(), [new FileWriteCap("/tmp/work/out.txt")], WorkDir).Allowed.Should().BeTrue();
    }

    [Fact]
    public void DenyCapabilityTypeBeatsAutoAllow()
    {
        var engine = new PermissionEngine();
        engine.DenyCapabilityType<FileReadCap>();

        engine.Decide(Tool(), [new FileReadCap("/tmp/work/a.txt")], WorkDir).Allowed.Should().BeFalse();
    }

    [Fact]
    public void AllowCapabilityRuleMatchesByPattern()
    {
        var engine = new PermissionEngine();
        engine.AllowCapabilityRule<NetworkEgressCap>("api.example.com");

        engine.Decide(Tool(), [new NetworkEgressCap("api.example.com")], WorkDir).Allowed.Should().BeTrue();
        engine.Decide(Tool(), [new NetworkEgressCap("evil.example.com")], WorkDir).Allowed.Should().BeFalse();
    }

    [Fact]
    public void DenyCapabilityRuleBeatsAutoAllow()
    {
        var engine = new PermissionEngine();
        engine.DenyCapabilityRule<ProcessExecCap>("pwd");

        engine.Decide(Tool(), [new ProcessExecCap("pwd", [])], WorkDir).Allowed.Should().BeFalse();
    }

    [Fact]
    public void EveryCapabilityMustBeCovered()
    {
        var engine = new PermissionEngine();

        engine.Decide(Tool(), [new FileReadCap("/tmp/work/a.txt"), new MemoryCap("notes", "read")], WorkDir)
            .Allowed.Should().BeTrue();
        engine.Decide(Tool(), [new FileReadCap("/tmp/work/a.txt"), new FileWriteCap("/tmp/work/out.txt")], WorkDir)
            .Allowed.Should().BeFalse();
    }

    // ── K. Pipeline maps capability denial to PermissionDenied ───────────────

    [Fact]
    public async Task PipelineMapsCapabilityDenialToPermissionDenied()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool(
            "writer",
            _ => ToolResult.Success("wrote"),
            capabilities: (_, ctx) => [new FileWriteCap(Path.Combine(ctx.Session.WorkingDirectory, "out.txt"))]);
        registry.Register(tool);
        var pipeline = new ToolPipeline(registry, new PermissionEngine());
        var state = new SessionState("s", WorkDir, When);

        var result = await pipeline.ExecuteAsync(new ToolCall("c", "writer", "{}"), state, CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.PermissionDenied);
        tool.ExecutionCount.Should().Be(0);
        pipeline.StepLog.Should().Contain("permission");
        pipeline.StepLog.Should().NotContain("execute");
    }

    // ── L. Pipeline still succeeds for allowed capabilities ──────────────────

    [Fact]
    public async Task PipelineSucceedsForAllowedCapabilitiesAndRecordsTwelveSteps()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool(
            "reader",
            _ => ToolResult.Success("data"),
            isReadOnly: true,
            capabilities: (_, ctx) => [new FileReadCap(Path.Combine(ctx.Session.WorkingDirectory, "a.txt"))]);
        registry.Register(tool);
        var pipeline = new ToolPipeline(registry, new PermissionEngine());
        var state = new SessionState("s", WorkDir, When);

        var result = await pipeline.ExecuteAsync(new ToolCall("c", "reader", "{}"), state, CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Be("data");
        pipeline.StepLog.Should().Equal(
            "parse", "schema-validate", "sanity-check", "plan-mode-guard", "permission", "cache-lookup",
            "pre-hook", "execute", "post-hook", "artifact-store", "cache-write", "invalidate");
    }
}
