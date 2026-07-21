using System.Collections.Concurrent;
using System.Text.Json;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests;

public sealed class TurnExecutorTests
{
    [Fact]
    public async Task ExecuteBatchAsync_ReturnsResultsInOriginalCallOrder_WhenConcurrentToolsFinishOutOfOrder()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool("slow", async (_, _, cancellationToken) =>
        {
            await Task.Delay(80, cancellationToken);
            return ToolResult.Success("slow-result");
        }, isReadOnly: true, isConcurrencySafe: true));
        registry.Register(new FakeTool("fast", async (_, _, cancellationToken) =>
        {
            await Task.Delay(10, cancellationToken);
            return ToolResult.Success("fast-result");
        }, isReadOnly: true, isConcurrencySafe: true));
        var executor = CreateExecutor(registry);

        var results = await executor.ExecuteBatchAsync([
            Call("1", "slow"),
            Call("2", "fast")
        ], State(), CancellationToken.None);

        Assert.Collection(results,
            result => Assert.Equal("slow-result", result.Content),
            result => Assert.Equal("fast-result", result.Content));
    }

    [Fact]
    public async Task ExecuteBatchAsync_RunsReadOnlyConcurrencySafeToolsConcurrently()
    {
        var registry = new ToolRegistry();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        registry.Register(new FakeTool("first", async (_, _, cancellationToken) =>
        {
            Interlocked.Increment(ref started);
            await gate.Task.WaitAsync(cancellationToken);
            return ToolResult.Success("first-result");
        }, isReadOnly: true, isConcurrencySafe: true));
        registry.Register(new FakeTool("second", async (_, _, cancellationToken) =>
        {
            Interlocked.Increment(ref started);
            await gate.Task.WaitAsync(cancellationToken);
            return ToolResult.Success("second-result");
        }, isReadOnly: true, isConcurrencySafe: true));
        var executor = CreateExecutor(registry);

        var running = executor.ExecuteBatchAsync([
            Call("1", "first"),
            Call("2", "second")
        ], State(), CancellationToken.None).AsTask();
        await SpinUntilAsync(() => Volatile.Read(ref started) == 2);
        gate.SetResult();
        var results = await running;

        Assert.Equal(["first-result", "second-result"], results.Select(r => r.Content).ToArray());
    }

    [Fact]
    public async Task ExecuteBatchAsync_RunsWritableAndNonConcurrencySafeToolsSerially()
    {
        var registry = new ToolRegistry();
        var active = 0;
        var maxActive = 0;
        var order = new ConcurrentQueue<string>();
        registry.Register(TrackedTool("write", isReadOnly: false, isConcurrencySafe: true, active, max => maxActive = Math.Max(maxActive, max), order));
        registry.Register(TrackedTool("unsafe-read", isReadOnly: true, isConcurrencySafe: false, active, max => maxActive = Math.Max(maxActive, max), order));
        var executor = CreateExecutor(registry);

        var results = await executor.ExecuteBatchAsync([
            Call("1", "write"),
            Call("2", "unsafe-read")
        ], State(), CancellationToken.None);

        Assert.Equal(1, maxActive);
        Assert.Equal(["write:start", "write:end", "unsafe-read:start", "unsafe-read:end"], order.ToArray());
        Assert.Equal(["write-result", "unsafe-read-result"], results.Select(r => r.Content).ToArray());
    }

    [Fact]
    public async Task ExecuteBatchAsync_CancelsParallelSiblings_WhenOneParallelToolCrashes()
    {
        var registry = new ToolRegistry();
        var siblingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.Register(new FakeTool("sibling", async (_, _, cancellationToken) =>
        {
            siblingStarted.SetResult();
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return ToolResult.Success("should-not-complete");
        }, isReadOnly: true, isConcurrencySafe: true));
        registry.Register(new FakeTool("crasher", async (_, _, cancellationToken) =>
        {
            await siblingStarted.Task.WaitAsync(cancellationToken);
            return ToolResult.Crash("boom", "retry later");
        }, isReadOnly: true, isConcurrencySafe: true));
        var executor = CreateExecutor(registry);

        var results = await executor.ExecuteBatchAsync([
            Call("1", "sibling"),
            Call("2", "crasher")
        ], State(), CancellationToken.None);

        Assert.Equal(ToolResultKind.Cancelled, results[0].Kind);
        Assert.Contains("sibling abort", results[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ToolResultKind.Crash, results[1].Kind);
    }

    [Fact]
    public async Task ExecuteBatchAsync_UserCancellationReturnsNormalCancellationMessage_NotSiblingAbort()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool("slow", async (_, _, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return ToolResult.Success("should-not-complete");
        }, isReadOnly: true, isConcurrencySafe: true));
        var executor = CreateExecutor(registry);
        using var userCancellation = new CancellationTokenSource();
        await userCancellation.CancelAsync();

        var results = await executor.ExecuteBatchAsync([Call("1", "slow")], State(), userCancellation.Token);

        Assert.Equal(ToolResultKind.Cancelled, results[0].Kind);
        Assert.DoesNotContain("sibling abort", results[0].Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteBatchAsync_ConvertsUnexpectedExecutorExceptionsToCrashResults()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool("throws", (_, _, _) => throw new InvalidOperationException("kaboom")));
        var executor = CreateExecutor(registry);

        var results = await executor.ExecuteBatchAsync([Call("1", "throws")], State(), CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(ToolResultKind.Crash, results[0].Kind);
        Assert.Contains("kaboom", results[0].Content, StringComparison.Ordinal);
    }

    private static TurnExecutor CreateExecutor(ToolRegistry registry) =>
        new(registry, new ToolPipeline(registry, new PermissionEngine()));

    private static ToolCall Call(string id, string name) => new(id, name, "{}");

    private static SessionState State() => new("session-id", Environment.CurrentDirectory, DateTimeOffset.UnixEpoch);

    private static FakeTool TrackedTool(
        string name,
        bool isReadOnly,
        bool isConcurrencySafe,
        int active,
        Action<int> observeMaxActive,
        ConcurrentQueue<string> order) =>
        new(name, async (_, _, cancellationToken) =>
        {
            order.Enqueue($"{name}:start");
            var nowActive = Interlocked.Increment(ref active);
            observeMaxActive(nowActive);
            await Task.Delay(30, cancellationToken);
            Interlocked.Decrement(ref active);
            order.Enqueue($"{name}:end");
            return ToolResult.Success($"{name}-result");
        }, isReadOnly, isConcurrencySafe);

    private static async Task SpinUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(5, timeout.Token);
        }
    }
}
