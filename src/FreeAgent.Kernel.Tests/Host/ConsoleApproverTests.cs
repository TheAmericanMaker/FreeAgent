using FluentAssertions;
using FreeAgent.Host;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Host;

public sealed class ConsoleApproverTests
{
    [Fact]
    public void MergeAddsWholeTypeAllowRuleForNewCapability()
    {
        var merged = ConsoleApprover.MergeAllowRules(new PermissionConfig(), [new FileWriteCap("/work/out.txt")]);

        merged.Allow.Should().ContainSingle();
        merged.Allow![0].Capability.Should().Be("FileWriteCap");
        merged.Allow![0].Pattern.Should().BeNull(); // whole-type grant
    }

    [Fact]
    public void MergePreservesExistingRulesAndDoesNotDuplicate()
    {
        var existing = PermissionConfig.Parse("""
            { "allow": [ { "capability": "ProcessExecCap", "pattern": "npm" }, { "capability": "FileWriteCap" } ] }
            """);

        var merged = ConsoleApprover.MergeAllowRules(existing, [new FileWriteCap("/work/x")]);

        // FileWriteCap whole-type rule already present → no duplicate; ProcessExec rule preserved.
        merged.Allow.Should().HaveCount(2);
        merged.Allow!.Count(r => r.Capability == "FileWriteCap").Should().Be(1);
        merged.Allow!.Should().Contain(r => r.Capability == "ProcessExecCap" && r.Pattern == "npm");
    }

    [Fact]
    public void MergedConfigReappliesCleanlyThroughPermissionConfig()
    {
        // Round-trip: the merged config must validate and actually grant the capability.
        var merged = ConsoleApprover.MergeAllowRules(new PermissionConfig(), [new FileWriteCap("/work/x")]);
        var engine = new PermissionEngine();
        merged.ApplyTo(engine);

        var tool = new FakeTool("WriteFile", _ => ToolResult.Success("x"));
        engine.Decide(tool, [new FileWriteCap("/anywhere/out.txt")], "/work").Allowed.Should().BeTrue();
    }
}
