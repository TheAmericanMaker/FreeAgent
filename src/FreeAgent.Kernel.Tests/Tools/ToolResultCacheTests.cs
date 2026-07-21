using System.Text.Json;
using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Tools;

public sealed class ToolResultCacheTests
{
    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static ToolPipeline Pipeline(IToolRegistry registry, IToolResultCache cache, IPermissionEngine? engine = null) =>
        new(registry, engine ?? new PermissionEngine(), approver: null, cache: cache);

    private static SessionState State() => new("s", "/tmp/work", DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task ReadOnlyCallExecutesOnceAndIsServedFromCacheOnReplay()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool("read", _ => ToolResult.Success("payload"), isReadOnly: true, isConcurrencySafe: true);
        registry.Register(tool);
        var cache = new InMemoryToolResultCache();
        var pipeline = Pipeline(registry, cache);
        var state = State();

        var first = await pipeline.ExecuteAsync(new ToolCall("c1", "read", "{}"), state, CancellationToken.None);
        var second = await pipeline.ExecuteAsync(new ToolCall("c2", "read", "{}"), state, CancellationToken.None);

        tool.ExecutionCount.Should().Be(1);
        first.Content.Should().Be("payload");
        second.Content.Should().Be("payload");
        cache.Count.Should().Be(1);

        // The cache-hit path short-circuits before the execute step.
        pipeline.StepLog.Count(s => s == "execute").Should().Be(1);
        pipeline.StepLog.Count(s => s == "cache-lookup").Should().Be(2);
    }

    [Fact]
    public async Task DifferentArgumentsProduceDifferentCacheEntries()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool("read", args => ToolResult.Success(args.RootElement.GetProperty("k").GetString() ?? ""), isReadOnly: true,
            schemaJson: "{\"type\":\"object\",\"required\":[\"k\"],\"properties\":{\"k\":{\"type\":\"string\"}}}");
        registry.Register(tool);
        var cache = new InMemoryToolResultCache();
        var pipeline = Pipeline(registry, cache);
        var state = State();

        await pipeline.ExecuteAsync(new ToolCall("c1", "read", Json(new { k = "a" })), state, CancellationToken.None);
        await pipeline.ExecuteAsync(new ToolCall("c2", "read", Json(new { k = "b" })), state, CancellationToken.None);
        await pipeline.ExecuteAsync(new ToolCall("c3", "read", Json(new { k = "a" })), state, CancellationToken.None); // hit

        tool.ExecutionCount.Should().Be(2);
        cache.Count.Should().Be(2);
    }

    [Fact]
    public async Task WhitespaceVariantArgumentsCanonicaliseToSameCacheKey()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool("read", _ => ToolResult.Success("x"), isReadOnly: true);
        registry.Register(tool);
        var cache = new InMemoryToolResultCache();
        var pipeline = Pipeline(registry, cache);

        await pipeline.ExecuteAsync(new ToolCall("c1", "read", "{\"a\":1}"), State(), CancellationToken.None);
        await pipeline.ExecuteAsync(new ToolCall("c2", "read", "{ \"a\" : 1 }"), State(), CancellationToken.None);

        tool.ExecutionCount.Should().Be(1);
    }

    [Fact]
    public async Task WritableToolBypassesCacheLookupAndIsNeverCached()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool("write", _ => ToolResult.Success("ok"), isReadOnly: false);
        registry.Register(tool);
        var engine = new PermissionEngine();
        engine.AllowTool("write"); // permit it
        var cache = new InMemoryToolResultCache();
        var pipeline = Pipeline(registry, cache, engine);

        await pipeline.ExecuteAsync(new ToolCall("c1", "write", "{}"), State(), CancellationToken.None);
        await pipeline.ExecuteAsync(new ToolCall("c2", "write", "{}"), State(), CancellationToken.None);

        tool.ExecutionCount.Should().Be(2);
        cache.Count.Should().Be(0);
    }

    [Fact]
    public async Task SuccessfulMutationInvalidatesEveryCachedRead()
    {
        var registry = new ToolRegistry();
        var read = new FakeTool("read", _ => ToolResult.Success("x"), isReadOnly: true);
        var write = new FakeTool("write", _ => ToolResult.Success("ok"), isReadOnly: false);
        registry.Register(read);
        registry.Register(write);
        var engine = new PermissionEngine();
        engine.AllowTool("write");
        var cache = new InMemoryToolResultCache();
        var pipeline = Pipeline(registry, cache, engine);

        await pipeline.ExecuteAsync(new ToolCall("c1", "read", "{}"), State(), CancellationToken.None);
        cache.Count.Should().Be(1);

        await pipeline.ExecuteAsync(new ToolCall("c2", "write", "{}"), State(), CancellationToken.None);
        cache.Count.Should().Be(0); // mutation invalidated all reads

        await pipeline.ExecuteAsync(new ToolCall("c3", "read", "{}"), State(), CancellationToken.None);
        read.ExecutionCount.Should().Be(2); // had to re-execute after invalidation
    }

    [Fact]
    public async Task EmptyResultIsNotCached()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool("read", _ => ToolResult.Success(""), isReadOnly: true); // becomes Empty
        registry.Register(tool);
        var cache = new InMemoryToolResultCache();
        var pipeline = Pipeline(registry, cache);

        await pipeline.ExecuteAsync(new ToolCall("c1", "read", "{}"), State(), CancellationToken.None);
        await pipeline.ExecuteAsync(new ToolCall("c2", "read", "{}"), State(), CancellationToken.None);

        cache.Count.Should().Be(0);
        tool.ExecutionCount.Should().Be(2);
    }

    [Fact]
    public async Task PipelineWithoutCacheBehavesAsBefore()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool("read", _ => ToolResult.Success("payload"), isReadOnly: true);
        registry.Register(tool);
        var pipeline = new ToolPipeline(registry, new PermissionEngine()); // no cache

        await pipeline.ExecuteAsync(new ToolCall("c1", "read", "{}"), State(), CancellationToken.None);
        await pipeline.ExecuteAsync(new ToolCall("c2", "read", "{}"), State(), CancellationToken.None);

        tool.ExecutionCount.Should().Be(2);
    }
}
