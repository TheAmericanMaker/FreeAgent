using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Tools.Adapters;

public sealed class AdapterToolTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static JsonDocument Args(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value));

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static ToolContext Context(string workingDirectory) =>
        new(new SessionState("adapter-session", workingDirectory, DateTimeOffset.UnixEpoch));

    /// <summary>A unique on-disk directory that is deleted when the test finishes.</summary>
    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "freeagent-tests", Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ── ReadFileTool ─────────────────────────────────────────────────────────

    [Fact]
    public void ReadFileFlagsAreReadOnlyAndConcurrencySafe()
    {
        var tool = new ReadFileTool();
        tool.Name.Should().Be("ReadFile");
        tool.IsReadOnly.Should().BeTrue();
        tool.IsConcurrencySafe.Should().BeTrue();
    }

    [Fact]
    public async Task ReadFileReturnsContentOfExistingFile()
    {
        using var work = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(work.Root, "data.txt"), "hello world");
        var tool = new ReadFileTool();

        var result = await tool.ExecuteAsync(Args(new { path = "data.txt" }), Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Be("hello world");
    }

    [Fact]
    public async Task ReadFileResolvesPathRelativeToWorkingDirectory()
    {
        using var work = new TempWorkspace();
        Directory.CreateDirectory(Path.Combine(work.Root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(work.Root, "sub", "nested.txt"), "deep");
        var tool = new ReadFileTool();

        var result = await tool.ExecuteAsync(Args(new { path = "sub/nested.txt" }), Context(work.Root), CancellationToken.None);

        result.Content.Should().Be("deep");
    }

    [Fact]
    public async Task ReadFileReturnsErrorWhenFileMissing()
    {
        using var work = new TempWorkspace();
        var tool = new ReadFileTool();

        var result = await tool.ExecuteAsync(Args(new { path = "absent.txt" }), Context(work.Root), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Kind.Should().Be(ToolResultKind.InvalidInput);
        result.Content.Should().ContainEquivalentOf("not found");
    }

    [Fact]
    public void ReadFileRequiredCapabilityIsFileReadCapWithResolvedAbsolutePath()
    {
        using var work = new TempWorkspace();
        var tool = new ReadFileTool();

        var capabilities = tool.RequiredCapabilities(Args(new { path = "data.txt" }), Context(work.Root));

        var cap = capabilities.Should().ContainSingle().Which.Should().BeOfType<FileReadCap>().Subject;
        cap.Path.Should().Be(Path.GetFullPath(Path.Combine(work.Root, "data.txt")));
    }

    // ── WriteFileTool ────────────────────────────────────────────────────────

    [Fact]
    public void WriteFileFlagsAreNotReadOnlyAndNotConcurrencySafe()
    {
        var tool = new WriteFileTool();
        tool.Name.Should().Be("WriteFile");
        tool.IsReadOnly.Should().BeFalse();
        tool.IsConcurrencySafe.Should().BeFalse();
    }

    [Fact]
    public async Task WriteFileWritesContentAndReportsPathAndByteCount()
    {
        using var work = new TempWorkspace();
        var tool = new WriteFileTool();
        var target = Path.Combine(work.Root, "out.txt");

        var result = await tool.ExecuteAsync(Args(new { path = "out.txt", content = "abc" }), Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Contain(target).And.Contain("3 bytes");
        (await File.ReadAllTextAsync(target)).Should().Be("abc");
    }

    [Fact]
    public async Task WriteFileByteCountIsUtf8NotCharacterCount()
    {
        using var work = new TempWorkspace();
        var tool = new WriteFileTool();

        // "é" is one char but two UTF-8 bytes.
        var result = await tool.ExecuteAsync(Args(new { path = "accent.txt", content = "é" }), Context(work.Root), CancellationToken.None);

        result.Content.Should().Contain("2 bytes");
    }

    [Fact]
    public async Task WriteFileCreatesMissingParentDirectories()
    {
        using var work = new TempWorkspace();
        var tool = new WriteFileTool();

        var result = await tool.ExecuteAsync(Args(new { path = "a/b/c/out.txt", content = "x" }), Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        File.Exists(Path.Combine(work.Root, "a", "b", "c", "out.txt")).Should().BeTrue();
    }

    [Fact]
    public void WriteFileRequiredCapabilityIsFileWriteCapWithResolvedAbsolutePath()
    {
        using var work = new TempWorkspace();
        var tool = new WriteFileTool();

        var capabilities = tool.RequiredCapabilities(Args(new { path = "out.txt", content = "x" }), Context(work.Root));

        capabilities.Should().ContainSingle().Which.Should().BeOfType<FileWriteCap>()
            .Which.Path.Should().Be(Path.GetFullPath(Path.Combine(work.Root, "out.txt")));
    }

    // ── ProcessExecTool ──────────────────────────────────────────────────────

    [Fact]
    public void ProcessExecFlagsAreNotReadOnlyAndNotConcurrencySafe()
    {
        var tool = new ProcessExecTool();
        tool.Name.Should().Be("ProcessExec");
        tool.IsReadOnly.Should().BeFalse();
        tool.IsConcurrencySafe.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessExecRunsCommandAndCapturesStdoutAndExitCode()
    {
        using var work = new TempWorkspace();
        var tool = new ProcessExecTool();

        var result = await tool.ExecuteAsync(Args(new { command = "echo", args = new[] { "hello" } }), Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Contain("Exit code: 0").And.Contain("hello");
    }

    [Fact]
    public async Task ProcessExecReportsNonZeroExitCodeAndStderrAsSuccess()
    {
        using var work = new TempWorkspace();
        var tool = new ProcessExecTool();

        // `ls` on a missing path exits non-zero and writes to stderr; the tool ran fine, so the
        // call itself is a Success that carries the exit code and stderr for the model to read.
        var result = await tool.ExecuteAsync(
            Args(new { command = "ls", args = new[] { "/freeagent-does-not-exist-xyz" } }),
            Context(work.Root),
            CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Contain("Exit code:").And.Contain("--- stderr ---");
    }

    [Fact]
    public async Task ProcessExecReturnsErrorWhenBinaryDoesNotExist()
    {
        using var work = new TempWorkspace();
        var tool = new ProcessExecTool();

        var result = await tool.ExecuteAsync(Args(new { command = "freeagent-no-such-binary-xyz" }), Context(work.Root), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().ContainEquivalentOf("could not start");
    }

    [Fact]
    public async Task ProcessExecKillsAndReportsAProcessThatExceedsTheTimeout()
    {
        using var work = new TempWorkspace();
        var tool = new ProcessExecTool(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        var result = await tool.ExecuteAsync(Args(new { command = "sleep", args = new[] { "10" } }), Context(work.Root), CancellationToken.None);

        stopwatch.Stop();
        result.IsError.Should().BeTrue();
        result.Content.Should().ContainEquivalentOf("timed out");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ProcessExecPropagatesCallerCancellation()
    {
        using var work = new TempWorkspace();
        var tool = new ProcessExecTool();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        Func<Task> act = async () =>
            await tool.ExecuteAsync(Args(new { command = "sleep", args = new[] { "10" } }), Context(work.Root), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void ProcessExecSplitsCommandWithSpacesWhenNoArgsAreProvided()
    {
        using var work = new TempWorkspace();
        var tool = new ProcessExecTool();

        var capabilities = tool.RequiredCapabilities(Args(new { command = "git status" }), Context(work.Root));

        var cap = capabilities.Should().ContainSingle().Which.Should().BeOfType<ProcessExecCap>().Subject;
        cap.Binary.Should().Be("git");
        cap.Args.Should().Equal("status");
    }

    [Fact]
    public void ProcessExecDoesNotSplitCommandWhenArgsAreProvided()
    {
        using var work = new TempWorkspace();
        var tool = new ProcessExecTool();

        var capabilities = tool.RequiredCapabilities(Args(new { command = "echo", args = new[] { "a b" } }), Context(work.Root));

        var cap = (ProcessExecCap)capabilities[0];
        cap.Binary.Should().Be("echo");
        cap.Args.Should().Equal("a b");
    }

    // ── Integration: tools through the pipeline + real PermissionEngine ───────

    [Fact]
    public async Task ReadFileThroughPipelineIsAutoAllowedInsideWorkingDirectory()
    {
        using var work = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(work.Root, "data.txt"), "content");
        var registry = new ToolRegistry();
        registry.Register(new ReadFileTool());
        var pipeline = new ToolPipeline(registry, new PermissionEngine());
        var state = new SessionState("s", work.Root, DateTimeOffset.UnixEpoch);

        var result = await pipeline.ExecuteAsync(new ToolCall("c1", "ReadFile", Json(new { path = "data.txt" })), state, CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Be("content");
        pipeline.StepLog.Should().Contain("execute");
    }

    [Fact]
    public async Task WriteFileThroughPipelineIsDeniedWithoutAnAllowRule()
    {
        using var work = new TempWorkspace();
        var registry = new ToolRegistry();
        registry.Register(new WriteFileTool());
        var pipeline = new ToolPipeline(registry, new PermissionEngine());
        var state = new SessionState("s", work.Root, DateTimeOffset.UnixEpoch);

        var result = await pipeline.ExecuteAsync(
            new ToolCall("c1", "WriteFile", Json(new { path = "out.txt", content = "x" })),
            state,
            CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.PermissionDenied);
        File.Exists(Path.Combine(work.Root, "out.txt")).Should().BeFalse();
        pipeline.StepLog.Should().NotContain("execute");
    }

    [Fact]
    public async Task WriteFileThroughPipelineSucceedsWhenTheCapabilityTypeIsAllowed()
    {
        using var work = new TempWorkspace();
        var registry = new ToolRegistry();
        registry.Register(new WriteFileTool());
        var engine = new PermissionEngine();
        engine.AllowCapabilityType<FileWriteCap>();
        var pipeline = new ToolPipeline(registry, engine);
        var state = new SessionState("s", work.Root, DateTimeOffset.UnixEpoch);

        var result = await pipeline.ExecuteAsync(
            new ToolCall("c1", "WriteFile", Json(new { path = "out.txt", content = "data" })),
            state,
            CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        (await File.ReadAllTextAsync(Path.Combine(work.Root, "out.txt"))).Should().Be("data");
    }

    [Fact]
    public async Task ProcessExecThroughPipelineAutoAllowsASafeReadOnlyBinary()
    {
        using var work = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(work.Root, "marker.txt"), "");
        var registry = new ToolRegistry();
        registry.Register(new ProcessExecTool());
        var pipeline = new ToolPipeline(registry, new PermissionEngine());
        var state = new SessionState("s", work.Root, DateTimeOffset.UnixEpoch);

        var result = await pipeline.ExecuteAsync(new ToolCall("c1", "ProcessExec", Json(new { command = "ls" })), state, CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Contain("marker.txt");
    }

    [Fact]
    public async Task ProcessExecThroughPipelineDeniesAnUnlistedBinary()
    {
        using var work = new TempWorkspace();
        var registry = new ToolRegistry();
        registry.Register(new ProcessExecTool());
        var pipeline = new ToolPipeline(registry, new PermissionEngine());
        var state = new SessionState("s", work.Root, DateTimeOffset.UnixEpoch);

        var result = await pipeline.ExecuteAsync(
            new ToolCall("c1", "ProcessExec", Json(new { command = "echo", args = new[] { "hi" } })),
            state,
            CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.PermissionDenied);
        pipeline.StepLog.Should().NotContain("execute");
    }

    [Fact]
    public async Task ProcessExecThroughPipelineBlocksAHardBlockedBinaryBeforeExecuting()
    {
        using var work = new TempWorkspace();
        var registry = new ToolRegistry();
        registry.Register(new ProcessExecTool());
        var engine = new PermissionEngine();
        engine.AllowTool("ProcessExec"); // even an explicit allow cannot override a hard block
        var pipeline = new ToolPipeline(registry, engine);
        var state = new SessionState("s", work.Root, DateTimeOffset.UnixEpoch);

        var result = await pipeline.ExecuteAsync(
            new ToolCall("c1", "ProcessExec", Json(new { command = "sudo", args = new[] { "ls" } })),
            state,
            CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.PermissionDenied);
        result.Content.Should().ContainEquivalentOf("blocked");
        pipeline.StepLog.Should().NotContain("execute");
    }
}
