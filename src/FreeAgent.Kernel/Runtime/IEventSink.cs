namespace FreeAgent.Kernel;

public interface IEventSink
{
    void OnThinking(string delta);
    void OnText(string delta);
    void OnUsage(Usage usage);
}
