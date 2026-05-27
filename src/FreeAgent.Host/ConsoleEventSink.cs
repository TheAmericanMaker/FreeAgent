using System.Text;

namespace FreeAgent.Kernel;

/// <summary>
/// Simple console event sink that prints streamed text/thinking directly to stdout/stderr.
/// Usage events are silently logged for downstream diagnostics (e.g. token tracking).
/// </summary>
public sealed class ConsoleEventSink : IEventSink
{
    private readonly bool _verbose;

    public ConsoleEventSink(bool verbose = false) => _verbose = verbose;

    public void OnThinking(string delta)
    {
        if (_verbose)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(delta);
            Console.ResetColor();
        }
        else
        {
            // Suppress reasoning by default
        }
    }

    public void OnText(string delta) => Console.Write(delta);

    public void OnUsage(Usage usage)
    {
        if (_verbose)
            Console.WriteLine($"\n[Tokens: {usage.InputTokens} → {usage.OutputTokens}]");
    }
}
