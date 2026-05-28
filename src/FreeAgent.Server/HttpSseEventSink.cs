using System.Text;
using System.Text.Json;
using FreeAgent.Kernel;

namespace FreeAgent.Server;

/// <summary>
/// Routes a <see cref="SessionRuntime"/>'s events into an HTTP Server-Sent Events response. One
/// SSE event per <see cref="IEventSink"/> callback, named after the event type (<c>text</c>,
/// <c>thinking</c>, <c>tool_call</c>, <c>tool_result</c>, <c>usage</c>) and carrying a small JSON
/// payload. Writes are serialized on the lock so interleaved events from the runtime can't tear
/// the stream.
/// </summary>
public sealed class HttpSseEventSink : IEventSink
{
    private readonly HttpResponse _response;
    private readonly object _writeLock = new();

    public HttpSseEventSink(HttpResponse response) => _response = response;

    public void OnText(string chunk) => Emit("text", new { chunk });
    public void OnThinking(string chunk) => Emit("thinking", new { chunk });
    public void OnToolCall(string toolName, string arguments) => Emit("tool_call", new { tool = toolName, arguments });
    public void OnToolResult(string toolName, ToolResult result) => Emit("tool_result", new { tool = toolName, kind = result.Kind.ToString(), content = result.Content });
    public void OnUsage(Usage usage) => Emit("usage", new { input = usage.InputTokens, output = usage.OutputTokens });

    public async ValueTask WriteEventAsync(string @event, string jsonPayload)
    {
        var line = $"event: {@event}\ndata: {jsonPayload}\n\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        await _response.Body.WriteAsync(bytes);
        await _response.Body.FlushAsync();
    }

    private void Emit(string @event, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var line = $"event: {@event}\ndata: {json}\n\n";
        var bytes = Encoding.UTF8.GetBytes(line);

        // Synchronous fire-and-forget on the IEventSink callbacks (the runtime calls these on its
        // own thread); serialize via a lock so two events can't interleave their bytes mid-line.
        lock (_writeLock)
        {
            try
            {
                _response.Body.Write(bytes, 0, bytes.Length);
                _response.Body.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Client went away mid-turn; the runtime's cancellation token handles the rest.
            }
        }
    }
}
