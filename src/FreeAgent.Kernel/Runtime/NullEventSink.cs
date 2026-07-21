namespace FreeAgent.Kernel;

/// <summary>
/// <see cref="IEventSink"/> that drops every event — used for sub-agents so their streaming output
/// doesn't mix into the parent host's console (the sub-agent's final text is the tool's result).
/// </summary>
public sealed class NullEventSink : IEventSink
{
    public void OnThinking(string delta) { }
    public void OnText(string delta) { }
    public void OnUsage(Usage usage) { }
}
