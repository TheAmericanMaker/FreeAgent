using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests;

public sealed class KernelHarness
{
    private KernelHarness(
        FakeProvider provider,
        ToolRegistry registry,
        PermissionEngine permissionEngine,
        ToolPipeline pipeline,
        InMemorySessionStore store,
        RecordingEventSink events,
        SessionState state,
        SessionRuntime runtime)
    {
        Provider = provider;
        Registry = registry;
        PermissionEngine = permissionEngine;
        Pipeline = pipeline;
        Store = store;
        Events = events;
        State = state;
        Runtime = runtime;
    }

    public FakeProvider Provider { get; }
    public ToolRegistry Registry { get; }
    public PermissionEngine PermissionEngine { get; }
    public ToolPipeline Pipeline { get; }
    public InMemorySessionStore Store { get; }
    public RecordingEventSink Events { get; }
    public SessionState State { get; }
    public SessionRuntime Runtime { get; }

    public static KernelHarness Create(params IReadOnlyList<StreamChunk>[] scripts)
    {
        var provider = new FakeProvider(scripts);
        var registry = new ToolRegistry();
        var permissions = new PermissionEngine();
        var pipeline = new ToolPipeline(registry, permissions);
        var store = new InMemorySessionStore();
        var events = new RecordingEventSink();
        var state = new SessionState("test-session", "/tmp/freeagent-kernel", DateTimeOffset.Parse("2026-05-25T00:00:00Z"));
        var runtime = new SessionRuntime(provider, registry, pipeline, store, events, state);
        return new KernelHarness(provider, registry, permissions, pipeline, store, events, state, runtime);
    }
}
