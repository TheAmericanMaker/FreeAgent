using System.Text.Json;
using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Tools;

public sealed class ArtifactStoreTests
{
    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static SessionState State() => new("s", "/tmp/work", DateTimeOffset.UnixEpoch);

    // ── InMemoryArtifactStore ───────────────────────────────────────────────

    [Fact]
    public void StoreAndRetrieveRoundTrip()
    {
        var store = new InMemoryArtifactStore();

        var refId = store.Store("hello world");

        store.TryGet(refId, out var content).Should().BeTrue();
        content.Should().Be("hello world");
        store.Count.Should().Be(1);
    }

    [Fact]
    public void UnknownReferenceReturnsFalse()
    {
        var store = new InMemoryArtifactStore();
        store.TryGet("nope", out _).Should().BeFalse();
    }

    // ── Pipeline integration ───────────────────────────────────────────────

    [Fact]
    public async Task LargeSuccessIsOffloadedAndReplacedWithPreviewAndRef()
    {
        var bigOutput = new string('x', 15_000);
        var registry = new ToolRegistry();
        registry.Register(new FakeTool("big", _ => ToolResult.Success(bigOutput), isReadOnly: true));
        var store = new InMemoryArtifactStore();
        var pipeline = new ToolPipeline(
            registry, new PermissionEngine(),
            artifacts: store, artifactThreshold: 10_000);

        var result = await pipeline.ExecuteAsync(new ToolCall("c1", "big", "{}"), State(), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Contain("15000 chars").And.Contain("ReadArtifact");
        result.Content.Length.Should().BeLessThan(bigOutput.Length); // it's a preview
        store.Count.Should().Be(1);
    }

    [Fact]
    public async Task SmallSuccessIsLeftAlone()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool("small", _ => ToolResult.Success("brief"), isReadOnly: true));
        var store = new InMemoryArtifactStore();
        var pipeline = new ToolPipeline(
            registry, new PermissionEngine(), artifacts: store, artifactThreshold: 100);

        var result = await pipeline.ExecuteAsync(new ToolCall("c1", "small", "{}"), State(), CancellationToken.None);

        result.Content.Should().Be("brief");
        store.Count.Should().Be(0);
    }

    [Fact]
    public async Task ErrorResultsAreNeverOffloaded()
    {
        var bigError = new string('!', 15_000);
        var registry = new ToolRegistry();
        registry.Register(new FakeTool("err", _ => ToolResult.Error(bigError), isReadOnly: true));
        var store = new InMemoryArtifactStore();
        var pipeline = new ToolPipeline(
            registry, new PermissionEngine(), artifacts: store, artifactThreshold: 10_000);

        var result = await pipeline.ExecuteAsync(new ToolCall("c1", "err", "{}"), State(), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput); // Error factory returns InvalidInput
        result.Content.Length.Should().Be(bigError.Length); // untouched
        store.Count.Should().Be(0);
    }

    // ── ReadArtifactTool ───────────────────────────────────────────────────

    [Fact]
    public async Task ReadArtifactFetchesStoredContent()
    {
        var store = new InMemoryArtifactStore();
        var refId = store.Store("the full payload");
        var tool = new ReadArtifactTool(store);

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse(Json(new { @ref = refId })),
            new ToolContext(State()), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Be("the full payload");
    }

    [Fact]
    public async Task ReadArtifactUnknownRefIsInvalidInput()
    {
        var tool = new ReadArtifactTool(new InMemoryArtifactStore());

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse(Json(new { @ref = "missing" })),
            new ToolContext(State()), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
    }
}
