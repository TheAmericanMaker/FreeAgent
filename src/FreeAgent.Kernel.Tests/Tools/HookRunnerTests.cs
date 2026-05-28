using System.Text.Json;
using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Tools;

public sealed class HookRunnerTests
{
    private sealed class FakeShell : IShellExecutor
    {
        public List<string> Commands { get; } = new();
        public Func<string, int>? ExitFor { get; init; }
        public bool ThrowOnRun { get; init; }

        public ValueTask<int> RunAsync(string command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            if (ThrowOnRun) throw new InvalidOperationException("boom");
            return ValueTask.FromResult(ExitFor?.Invoke(command) ?? 0);
        }
    }

    // ── Matches ─────────────────────────────────────────────────────────────

    [Fact]
    public void NullConditionAlwaysMatches() =>
        HookRunner.Matches(null, "AnyTool", "{}").Should().BeTrue();

    [Theory]
    [InlineData("WriteFile", "WriteFile", true)]
    [InlineData("WriteFile", "ReadFile", false)]
    public void ToolConditionMatchesByName(string condition, string toolName, bool expected) =>
        HookRunner.Matches(new HookCondition(Tool: condition), toolName, "{}").Should().Be(expected);

    [Theory]
    [InlineData("rm",   "{\"command\":\"rm -rf x\"}", true)]
    [InlineData("rm",   "{\"command\":\"ls\"}",        false)]
    public void InputContainsConditionMatchesSubstring(string substr, string args, bool expected) =>
        HookRunner.Matches(new HookCondition(InputContains: substr), "ProcessExec", args).Should().Be(expected);

    [Fact]
    public void BothConditionsMustMatch()
    {
        var cond = new HookCondition(Tool: "ProcessExec", InputContains: "rm");
        HookRunner.Matches(cond, "ProcessExec", "{\"command\":\"rm -rf x\"}").Should().BeTrue();
        HookRunner.Matches(cond, "WriteFile",   "{\"command\":\"rm -rf x\"}").Should().BeFalse();
        HookRunner.Matches(cond, "ProcessExec", "{\"command\":\"ls\"}").Should().BeFalse();
    }

    // ── Substitute ──────────────────────────────────────────────────────────

    [Fact]
    public void SubstituteReplacesToolNameAndInput()
    {
        var cmd = HookRunner.Substitute("echo {{tool_name}} :: {{tool_input}}", "WriteFile", "{\"path\":\"a\"}");
        cmd.Should().Be("echo WriteFile :: {\"path\":\"a\"}");
    }

    [Fact]
    public void SubstituteTruncatesVeryLongInput()
    {
        var big = new string('x', 5000);
        var cmd = HookRunner.Substitute("echo {{tool_input}}", "Tool", big);
        cmd.Length.Should().BeLessThan(big.Length); // truncated with an ellipsis
        cmd.Should().EndWith("…");
    }

    // ── Dispatch ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PreToolHookRunsForMatchingToolAndIsSkippedOtherwise()
    {
        var shell = new FakeShell();
        var config = new HooksConfig(PreToolUse:
        [
            new HookSpec("echo write", new HookCondition(Tool: "WriteFile")),
        ]);
        var runner = new HookRunner(config, shell);

        await runner.RunPreToolAsync("WriteFile", "{}", CancellationToken.None);
        await runner.RunPreToolAsync("ReadFile",  "{}", CancellationToken.None);

        shell.Commands.Should().HaveCount(1);
        shell.Commands[0].Should().Be("echo write");
    }

    [Fact]
    public async Task PostToolHookSubstitutionsAreApplied()
    {
        var shell = new FakeShell();
        var config = new HooksConfig(PostToolUse:
        [
            new HookSpec("echo {{tool_name}}: {{tool_input}}"),
        ]);
        var runner = new HookRunner(config, shell);

        await runner.RunPostToolAsync("ReadFile", "{\"path\":\"a\"}", ToolResult.Success("ok"), CancellationToken.None);

        shell.Commands.Should().ContainSingle().Which.Should().Be("echo ReadFile: {\"path\":\"a\"}");
    }

    [Fact]
    public async Task ShellFailuresAreSwallowedSoTheAgentIsNotBlocked()
    {
        var shell = new FakeShell { ThrowOnRun = true };
        var config = new HooksConfig(PreToolUse: [new HookSpec("anything")]);
        var runner = new HookRunner(config, shell);

        var act = async () => await runner.RunPreToolAsync("X", "{}", CancellationToken.None);

        await act.Should().NotThrowAsync();
        shell.Commands.Should().ContainSingle(); // attempted, then swallowed
    }

    // ── Pipeline integration ────────────────────────────────────────────────

    [Fact]
    public async Task PipelineCallsPreAndPostHooksAroundExecute()
    {
        var shell = new FakeShell();
        var hooks = new HookRunner(
            new HooksConfig(
                PreToolUse:  [new HookSpec("PRE {{tool_name}}")],
                PostToolUse: [new HookSpec("POST {{tool_name}}")]),
            shell);

        var registry = new ToolRegistry();
        var tool = new FakeTool("ping", _ => ToolResult.Success("pong"), isReadOnly: true);
        registry.Register(tool);
        var pipeline = new ToolPipeline(registry, new PermissionEngine(), approver: null, cache: null, hooks: hooks);

        await pipeline.ExecuteAsync(new ToolCall("c1", "ping", "{}"),
            new SessionState("s", "/tmp", DateTimeOffset.UnixEpoch),
            CancellationToken.None);

        shell.Commands.Should().Equal("PRE ping", "POST ping");
        pipeline.StepLog.Should().Contain("pre-hook").And.Contain("execute").And.Contain("post-hook");
    }

    // ── Config loading ──────────────────────────────────────────────────────

    [Fact]
    public void HooksParseFromPermissionConfigJson()
    {
        var config = PermissionConfig.Parse("""
            {
              "hooks": {
                "preToolUse":  [ { "if": { "tool": "ProcessExec", "inputContains": "rm" }, "run": "echo dangerous" } ],
                "postToolUse": [ { "run": "echo done" } ]
              }
            }
            """);

        config.Hooks.Should().NotBeNull();
        config.Hooks!.PreToolUse.Should().ContainSingle();
        config.Hooks.PreToolUse![0].If!.Tool.Should().Be("ProcessExec");
        config.Hooks.PostToolUse.Should().ContainSingle();
    }
}
