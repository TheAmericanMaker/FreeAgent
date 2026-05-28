using System.Text.Json;
using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Permissions;

public sealed class PermissionConfigTests
{
    private static readonly ITool WriteTool = new FakeTool("WriteFile", _ => ToolResult.Success("ok"));
    private const string WorkingDir = "/home/u/project";

    [Fact]
    public void AllowRuleForCapabilityTypeGrantsAPreviouslyDeniedWrite()
    {
        var engine = new PermissionEngine();
        var outsidePath = "/var/data/out.txt"; // outside the workspace → not auto-allowed

        // Baseline: a write outside the workspace is denied.
        engine.Decide(WriteTool, [new FileWriteCap(outsidePath)], WorkingDir).Allowed.Should().BeFalse();

        PermissionConfig.Parse("""{ "allow": [ { "capability": "FileWriteCap" } ] }""").ApplyTo(engine);

        engine.Decide(WriteTool, [new FileWriteCap(outsidePath)], WorkingDir).Allowed.Should().BeTrue();
    }

    [Fact]
    public void AllowRuleWithPatternMatchesTargetGlob()
    {
        var engine = new PermissionEngine();
        PermissionConfig.Parse("""{ "allow": [ { "capability": "FileWriteCap", "pattern": "/home/u/project/**" } ] }""").ApplyTo(engine);

        engine.Decide(WriteTool, [new FileWriteCap("/home/u/project/sub/out.txt")], WorkingDir).Allowed.Should().BeTrue();
        engine.Decide(WriteTool, [new FileWriteCap("/etc/passwd")], WorkingDir).Allowed.Should().BeFalse();
    }

    [Fact]
    public void DenyToolBeatsAnAllowRule()
    {
        var engine = new PermissionEngine();
        PermissionConfig.Parse("""
            { "denyTools": ["WriteFile"], "allow": [ { "capability": "FileWriteCap" } ] }
            """).ApplyTo(engine);

        engine.Decide(WriteTool, [new FileWriteCap("/home/u/project/out.txt")], WorkingDir).Allowed.Should().BeFalse();
    }

    [Fact]
    public void HardSecurityBlockStillWinsOverConfigAllow()
    {
        var engine = new PermissionEngine();
        var tool = new FakeTool("ProcessExec", _ => ToolResult.Success("x"));
        PermissionConfig.Parse("""{ "allowTools": ["ProcessExec"], "allow": [ { "capability": "ProcessExecCap" } ] }""").ApplyTo(engine);

        var decision = engine.Decide(tool, [new ProcessExecCap("sudo", ["ls"])], WorkingDir);
        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().ContainEquivalentOf("blocked");
    }

    [Fact]
    public void UnknownCapabilityNameThrows()
    {
        var act = () => PermissionConfig.Parse("""{ "allow": [ { "capability": "NotARealCap" } ] }""");
        act.Should().Throw<ArgumentException>().WithMessage("*NotARealCap*");
    }

    [Fact]
    public void EmptyConfigAppliesNoRules()
    {
        var engine = new PermissionEngine();
        var config = PermissionConfig.Parse("{}");
        config.RuleCount.Should().Be(0);
        config.ApplyTo(engine);

        // Default behavior unchanged: a write outside the workspace is still denied.
        engine.Decide(WriteTool, [new FileWriteCap("/var/out.txt")], WorkingDir).Allowed.Should().BeFalse();
    }

    [Fact]
    public void MalformedJsonThrowsJsonException()
    {
        var act = () => PermissionConfig.Parse("{ not json");
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void CommentsAndTrailingCommasAreTolerated()
    {
        var config = PermissionConfig.Parse("""
            {
              // grant project writes
              "allow": [ { "capability": "FileWriteCap", "pattern": "**" }, ],
            }
            """);
        config.RuleCount.Should().Be(1);
    }
}
