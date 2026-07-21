using System.Text.Json;
using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Tools.Adapters;

public sealed class MemoryToolsTests
{
    private static JsonDocument Args(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value));
    private static ToolContext Context() => new(new SessionState("s", "/tmp/work", DateTimeOffset.UnixEpoch));

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "freeagent-tests", Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    [Fact]
    public void FlagsAndCapabilitiesMatchReadAndWriteSemantics()
    {
        using var root = new TempRoot();
        var reader = new ReadMemoryTool(root.Path);
        var writer = new WriteMemoryTool(root.Path);

        reader.IsReadOnly.Should().BeTrue();
        reader.IsConcurrencySafe.Should().BeTrue();
        writer.IsReadOnly.Should().BeFalse();

        reader.RequiredCapabilities(Args(new { key = "x" }), Context())
            .Should().ContainSingle().Which.Should().BeOfType<MemoryCap>()
            .Which.Operation.Should().Be("read");
        writer.RequiredCapabilities(Args(new { key = "x", content = "y" }), Context())
            .Should().ContainSingle().Which.Should().BeOfType<MemoryCap>()
            .Which.Operation.Should().Be("write");
    }

    [Fact]
    public void ReadCapabilityIsAutoAllowedAndWriteIsNot()
    {
        var engine = new PermissionEngine();
        var tool = new FakeTool("x", _ => ToolResult.Success("ok"));

        engine.Decide(tool, [new MemoryCap("ns", "read")], "/work").Allowed.Should().BeTrue();
        engine.Decide(tool, [new MemoryCap("ns", "write")], "/work").Outcome
            .Should().Be(PermissionOutcome.Prompt); // requires approval
    }

    [Fact]
    public async Task WriteThenReadRoundTrip()
    {
        using var root = new TempRoot();
        var writer = new WriteMemoryTool(root.Path);
        var reader = new ReadMemoryTool(root.Path);

        var wrote = await writer.ExecuteAsync(
            Args(new { key = "preferences", content = "use 2-space indent" }),
            Context(), CancellationToken.None);
        wrote.Kind.Should().Be(ToolResultKind.Success);

        var read = await reader.ExecuteAsync(
            Args(new { key = "preferences" }), Context(), CancellationToken.None);
        read.Kind.Should().Be(ToolResultKind.Success);
        read.Content.Should().Be("use 2-space indent");
    }

    [Fact]
    public async Task ReadMissingKeyIsInvalidInput()
    {
        using var root = new TempRoot();
        var reader = new ReadMemoryTool(root.Path);

        var result = await reader.ExecuteAsync(Args(new { key = "absent" }), Context(), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
        result.Content.Should().ContainEquivalentOf("not found");
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("path/with/slash")]
    [InlineData("")]
    [InlineData("has spaces")]
    public async Task InvalidKeysAreRejected(string key)
    {
        using var root = new TempRoot();
        var reader = new ReadMemoryTool(root.Path);
        var writer = new WriteMemoryTool(root.Path);

        (await reader.ExecuteAsync(Args(new { key }), Context(), CancellationToken.None)).Kind
            .Should().Be(ToolResultKind.InvalidInput);
        (await writer.ExecuteAsync(Args(new { key, content = "x" }), Context(), CancellationToken.None)).Kind
            .Should().Be(ToolResultKind.InvalidInput);
    }
}
