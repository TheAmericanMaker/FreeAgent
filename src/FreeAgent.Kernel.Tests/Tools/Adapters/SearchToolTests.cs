using System.Text.Json;
using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Tools.Adapters;

public sealed class SearchToolTests
{
    private static JsonDocument Args(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value));
    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static ToolContext Context(string workingDirectory) =>
        new(new SessionState("search-session", workingDirectory, DateTimeOffset.UnixEpoch));

    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "freeagent-tests", Guid.NewGuid().ToString("N"))).FullName;

        public void Write(string relativePath, string content)
        {
            var full = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ── GlobTool ───────────────────────────────────────────────────────────────

    [Fact]
    public void GlobFlagsAreReadOnlyAndConcurrencySafe()
    {
        var tool = new GlobTool();
        tool.Name.Should().Be("Glob");
        tool.IsReadOnly.Should().BeTrue();
        tool.IsConcurrencySafe.Should().BeTrue();
        tool.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GlobMatchesNestedFilesWithDoubleStar()
    {
        using var work = new TempWorkspace();
        work.Write("a.cs", "x");
        work.Write("src/b.cs", "x");
        work.Write("src/deep/c.cs", "x");
        work.Write("notes.txt", "x");
        var tool = new GlobTool();

        var result = await tool.ExecuteAsync(Args(new { pattern = "**/*.cs" }), Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        var lines = result.Content.Split('\n');
        lines.Should().BeEquivalentTo("a.cs", "src/b.cs", "src/deep/c.cs");
        result.Content.Should().NotContain("notes.txt");
    }

    [Fact]
    public async Task GlobSkipsNoiseDirectories()
    {
        using var work = new TempWorkspace();
        work.Write("keep.cs", "x");
        work.Write(".git/config.cs", "x");
        work.Write("obj/generated.cs", "x");
        var tool = new GlobTool();

        var result = await tool.ExecuteAsync(Args(new { pattern = "**/*.cs" }), Context(work.Root), CancellationToken.None);

        result.Content.Split('\n').Should().BeEquivalentTo("keep.cs");
    }

    [Fact]
    public async Task GlobReturnsEmptyWhenNothingMatches()
    {
        using var work = new TempWorkspace();
        work.Write("a.txt", "x");
        var tool = new GlobTool();

        var result = await tool.ExecuteAsync(Args(new { pattern = "**/*.cs" }), Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Empty);
    }

    [Fact]
    public void GlobRequiresFileReadCapForResolvedRoot()
    {
        using var work = new TempWorkspace();
        var tool = new GlobTool();

        var caps = tool.RequiredCapabilities(Args(new { pattern = "*", path = "src" }), Context(work.Root));

        caps.Should().ContainSingle().Which.Should().BeOfType<FileReadCap>()
            .Which.Path.Should().Be(Path.GetFullPath(Path.Combine(work.Root, "src")));
    }

    // ── GrepTool ───────────────────────────────────────────────────────────────

    [Fact]
    public void GrepFlagsAreReadOnlyAndConcurrencySafe()
    {
        var tool = new GrepTool();
        tool.Name.Should().Be("Grep");
        tool.IsReadOnly.Should().BeTrue();
        tool.IsConcurrencySafe.Should().BeTrue();
        tool.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GrepFindsMatchingLinesWithPathAndLineNumber()
    {
        using var work = new TempWorkspace();
        work.Write("a.cs", "class Foo\nclass Bar\n");
        work.Write("b.cs", "nothing here\n");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(Args(new { pattern = "class Foo" }), Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Be("a.cs:1:class Foo");
    }

    [Fact]
    public async Task GrepGlobFilterRestrictsSearchedFiles()
    {
        using var work = new TempWorkspace();
        work.Write("a.cs", "target\n");
        work.Write("a.txt", "target\n");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(Args(new { pattern = "target", glob = "*.cs" }), Context(work.Root), CancellationToken.None);

        result.Content.Should().Contain("a.cs:1:target").And.NotContain("a.txt");
    }

    [Fact]
    public async Task GrepIgnoreCaseHonoured()
    {
        using var work = new TempWorkspace();
        work.Write("a.cs", "HELLO\n");
        var tool = new GrepTool();

        var insensitive = await tool.ExecuteAsync(Args(new { pattern = "hello", ignore_case = true }), Context(work.Root), CancellationToken.None);
        var sensitive = await tool.ExecuteAsync(Args(new { pattern = "hello", ignore_case = false }), Context(work.Root), CancellationToken.None);

        insensitive.Kind.Should().Be(ToolResultKind.Success);
        sensitive.Kind.Should().Be(ToolResultKind.Empty);
    }

    [Fact]
    public async Task GrepSkipsBinaryFiles()
    {
        using var work = new TempWorkspace();
        work.Write("text.cs", "match\n");
        work.Write("blob.bin", "match\0and-binary");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(Args(new { pattern = "match" }), Context(work.Root), CancellationToken.None);

        result.Content.Should().Contain("text.cs:1:match").And.NotContain("blob.bin");
    }

    [Fact]
    public async Task GrepReturnsInvalidInputForBadRegex()
    {
        using var work = new TempWorkspace();
        work.Write("a.cs", "x\n");
        var tool = new GrepTool();

        var result = await tool.ExecuteAsync(Args(new { pattern = "[unclosed" }), Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
    }

    // ── Through the pipeline + real PermissionEngine ─────────────────────────────

    [Fact]
    public async Task GrepThroughPipelineIsAutoAllowedInsideWorkingDirectory()
    {
        using var work = new TempWorkspace();
        work.Write("a.cs", "needle\n");
        var registry = new ToolRegistry();
        registry.Register(new GrepTool());
        var pipeline = new ToolPipeline(registry, new PermissionEngine());
        var state = new SessionState("s", work.Root, DateTimeOffset.UnixEpoch);

        var result = await pipeline.ExecuteAsync(new ToolCall("c1", "Grep", Json(new { pattern = "needle" })), state, CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Contain("a.cs:1:needle");
        pipeline.StepLog.Should().Contain("execute");
    }

    [Fact]
    public async Task GlobThroughPipelineIsDeniedForRootOutsideWorkspace()
    {
        using var work = new TempWorkspace();
        using var outside = new TempWorkspace();
        var registry = new ToolRegistry();
        registry.Register(new GlobTool());
        var pipeline = new ToolPipeline(registry, new PermissionEngine());
        var state = new SessionState("s", work.Root, DateTimeOffset.UnixEpoch);

        var result = await pipeline.ExecuteAsync(
            new ToolCall("c1", "Glob", Json(new { pattern = "*", path = outside.Root })),
            state,
            CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.PermissionDenied);
        pipeline.StepLog.Should().NotContain("execute");
    }
}
