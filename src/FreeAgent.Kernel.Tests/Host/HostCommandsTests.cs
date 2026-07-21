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
        help.Should().Contain("/help").And.Contain("/status").And.Contain("/model").And.Contain("/plan")
            .And.Contain("/serve");
    }

    [Fact]
    public void ServeStartParsesModelPathPortAndBinDefaults()
    {
        var (args, err) = HostCommands.ParseServeStart(["/serve", "start", "/m/qwen.gguf"]);
        err.Should().BeNull();
        args.Should().NotBeNull();
        args!.ModelPath.Should().Be("/m/qwen.gguf");
        args.Port.Should().Be(8080);
        args.BinPath.Should().Be("llama-server");
        args.ExtraArgs.Should().BeEmpty();
    }

    [Fact]
    public void ServeStartParsesPortAndBinFlags()
    {
        var (args, err) = HostCommands.ParseServeStart(
            ["/serve", "start", "/m/qwen.gguf", "--port", "9001", "--bin", "/opt/llama-server"]);
        err.Should().BeNull();
        args!.Port.Should().Be(9001);
        args.BinPath.Should().Be("/opt/llama-server");
    }

    [Fact]
    public void ServeStartCollectsExtraArgsAfterDoubleDash()
    {
        var (args, _) = HostCommands.ParseServeStart(
            ["/serve", "start", "/m/qwen.gguf", "--port", "8080", "--", "--ctx-size", "8192", "-ngl", "32"]);
        args!.ExtraArgs.Should().Be("--ctx-size 8192 -ngl 32");
    }

    [Fact]
    public void ServeStartRejectsBadPortUnknownFlagsAndMissingModel()
    {
        HostCommands.ParseServeStart(["/serve", "start"]).Error.Should().Contain("Usage");
        HostCommands.ParseServeStart(["/serve", "start", "/m/x", "--port", "not-a-number"]).Error.Should().Contain("--port");
        HostCommands.ParseServeStart(["/serve", "start", "/m/x", "--port", "70000"]).Error.Should().Contain("--port");
        HostCommands.ParseServeStart(["/serve", "start", "/m/x", "--whatever"]).Error.Should().Contain("Unknown flag");
        HostCommands.ParseServeStart(["/serve", "start", "/m/x", "/m/y"]).Error.Should().Contain("Unexpected argument");
    }

    [Fact]
    public async Task ServeStatusOnFreshInstallReportsNotRunning()
    {
        if (File.Exists(ModelServerLauncher.PidFile()))
            File.Delete(ModelServerLauncher.PidFile()); // make sure we're starting clean

        var result = await HostCommands.Serve(["/serve", "status"]);
        result.Should().Be("Not running.");
    }

    [Fact]
    public async Task ServeStopWithNoPidFileReportsNotRunning()
    {
        if (File.Exists(ModelServerLauncher.PidFile()))
            File.Delete(ModelServerLauncher.PidFile());

        var result = await HostCommands.Serve(["/serve", "stop"]);
        result.Should().Contain("Not running");
    }

    [Fact]
    public async Task ServeRejectsUnknownSubcommands()
    {
        (await HostCommands.Serve(["/serve"])).Should().Contain("Usage");
        (await HostCommands.Serve(["/serve", "burn"])).Should().Contain("Unknown /serve");
    }

    [Fact]
    public void ServeDownloadParsesUrlAndOptionalName()
    {
        var (args, err) = HostCommands.ParseServeDownload(["/serve", "download", "hf:o/r/m.gguf", "--name", "local.gguf"]);
        err.Should().BeNull();
        args!.Source.Should().Be("hf:o/r/m.gguf");
        args.OverrideName.Should().Be("local.gguf");
    }

    [Fact]
    public void ServeDownloadParsesBareUrlWithoutName()
    {
        var (args, err) = HostCommands.ParseServeDownload(["/serve", "download", "https://example/m.gguf"]);
        err.Should().BeNull();
        args!.Source.Should().Be("https://example/m.gguf");
        args.OverrideName.Should().BeNull();
    }

    [Fact]
    public void ServeDownloadRejectsMissingSourceUnknownFlagsAndDoubleSource()
    {
        HostCommands.ParseServeDownload(["/serve", "download"]).Error.Should().Contain("Usage");
        HostCommands.ParseServeDownload(["/serve", "download", "--name"]).Error.Should().Contain("--name");
        HostCommands.ParseServeDownload(["/serve", "download", "--whatever", "x"]).Error.Should().Contain("Unknown flag");
        HostCommands.ParseServeDownload(["/serve", "download", "a", "b"]).Error.Should().Contain("Unexpected argument");
    }

    [Fact]
    public void BuildDefaultRegistryRegistersEverySlashCommand()
    {
        var registry = HostCommands.BuildDefaultRegistry();

        registry.All.Select(c => c.Id).Should().Contain([
            "help", "status", "model", "plan.toggle", "undo", "revert",
            "tag", "untag", "doctor", "session.fork",
            "serve.start", "serve.stop", "serve.status",
            "run", "commands"
        ]);
    }

    [Fact]
    public void CommandsListWithNoFilterShowsEveryCategory()
    {
        var output = HostCommands.CommandsList(["/commands"]);

        output.Should().Contain("[Session]").And.Contain("[Plan]").And.Contain("[Editing]")
            .And.Contain("[Diagnostics]").And.Contain("[Local model]");
        output.Should().Contain("/fork").And.Contain("/serve start");
    }

    [Fact]
    public void CommandsListWithQueryFuzzyFilters()
    {
        var output = HostCommands.CommandsList(["/commands", "fk"]);

        output.Should().Contain("/fork").And.NotContain("/plan");
    }

    [Fact]
    public void CommandsListWithUnknownQueryReportsNoMatches()
    {
        var output = HostCommands.CommandsList(["/commands", "xyzzy"]);

        output.Should().Contain("No commands match");
    }

    [Fact]
    public async Task ForkOnEmptySessionRefuses()
    {
        var state = State();
        var result = await HostCommands.Fork(state);
        result.Should().Contain("Nothing to fork");
    }

    [Fact]
    public async Task ForkSnapshotsTranscriptToSeparateFileWithNewId()
    {
        // Use a temp directory so the test doesn't leave a session-fork-*.jsonl alongside the repo.
        var tempDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "freeagent-fork-tests", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var state = new SessionState("orig1234", tempDir, DateTimeOffset.UnixEpoch);
            state.Messages.Add(new Message(MessageRole.System, "sys"));
            state.Messages.Add(new Message(MessageRole.User, "hi"));
            state.Messages.Add(new Message(MessageRole.Assistant, "hello"));

            var result = await HostCommands.Fork(state);
            result.Should().Contain("Forked").And.Contain("3 message").And.Contain("--resume");

            var forkFiles = Directory.GetFiles(tempDir, "session-fork-*.jsonl");
            forkFiles.Should().ContainSingle();

            var forkPath = forkFiles[0];
            var loadStore = new JsonlSessionStore(path: forkPath);
            var jsonl = await File.ReadAllTextAsync(forkPath);
            var loaded = await loadStore.DeserializeAsync(jsonl, default);

            loaded.SessionId.Should().NotBe("orig1234");
            loaded.SessionId.Should().HaveLength(8);
            loaded.WorkingDirectory.Should().Be(tempDir);
            loaded.Messages.Should().HaveCount(3);
            loaded.Messages.Select(m => m.Content).Should().Equal("sys", "hi", "hello");

            // Original state is untouched (no rename, no message mutation).
            state.SessionId.Should().Be("orig1234");
            state.Messages.Should().HaveCount(3);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (IOException) { }
        }
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
    public void TagAddsAndUntagRemovesAndStatusShowsThem()
    {
        var state = State();

        HostCommands.Tag(state, ["/tag", "wip"]).Should().Contain("Tagged");
        HostCommands.Tag(state, ["/tag", "wip"]).Should().Contain("Already tagged");
        state.Tags.Should().Contain("wip");

        HostCommands.StatusText(state, "m").Should().Contain("wip");

        HostCommands.Untag(state, ["/untag", "wip"]).Should().Contain("Untagged");
        state.Tags.Should().BeEmpty();
        HostCommands.Untag(state, ["/untag", "wip"]).Should().Contain("No such tag");
    }

    [Fact]
    public void TagAndUntagWithoutNameShowUsage()
    {
        var state = State();
        HostCommands.Tag(state, ["/tag"]).Should().Contain("Usage");
        HostCommands.Untag(state, ["/untag"]).Should().Contain("Usage");
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
