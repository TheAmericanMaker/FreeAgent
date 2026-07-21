using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests;

public sealed class RecordingEventSink : IEventSink
{
    public List<string> Thinking { get; } = [];
    public List<string> Text { get; } = [];
    public List<Usage> Usage { get; } = [];
    public void OnThinking(string delta) => Thinking.Add(delta);
    public void OnText(string delta) => Text.Add(delta);
    public void OnUsage(Usage usage) => Usage.Add(usage);
}
