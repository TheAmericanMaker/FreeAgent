using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace FreeAgent.Kernel;

/// <summary>
/// Native Ollama streaming provider. Targets <c>POST {baseUrl}/api/chat</c>; Ollama also exposes a
/// reasonable <c>/v1/chat/completions</c> compatibility layer (the <see cref="OpenAIProvider"/>
/// already uses it), but this adapter exists for the cases that need Ollama-native control —
/// per-request <c>options</c> (e.g. <c>num_ctx</c>, <c>temperature</c>), and the more direct error
/// surfaces. Stream format is <b>newline-delimited JSON</b>, not SSE: each line is a complete chunk
/// (<c>{ "model":…, "message": { "role","content","tool_calls?" }, "done": bool, "prompt_eval_count"?, "eval_count"? }</c>).
/// </summary>
public sealed class OllamaProvider : IProvider, IDisposable
{
    public const string DefaultBaseUrl = "http://localhost:11434";
    public const string DefaultModel = "qwen2.5-coder";

    private readonly string _model;
    private readonly int? _numCtx;
    private readonly double? _temperature;
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly bool _ownsClient;
    private bool _disposed;

    public OllamaProvider(string baseUrl = DefaultBaseUrl, string model = DefaultModel, int? numCtx = null, double? temperature = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        _model = model;
        _numCtx = numCtx;
        _temperature = temperature;
        _ownsClient = true;
        _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
    }

    public OllamaProvider(HttpClient httpClient, string baseUrl, string model = DefaultModel, int? numCtx = null, double? temperature = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _model = model;
        _numCtx = numCtx;
        _temperature = temperature;
        _ownsClient = false;
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
    }

    public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
        ProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var body = BuildRequestBody(request);
        using var httpContent = new StringContent(body, Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "api/chat"))
        {
            Content = httpContent
        };
        var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Ollama API returned {(int)response.StatusCode} ({response.StatusCode}): {error}",
                inner: null,
                statusCode: response.StatusCode);
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var sawComplete = false;
        var nextSyntheticToolCallId = 0;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; } // ignore malformed chunks (matches OpenAI/Anthropic adapters)

            using (doc)
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
                {
                    if (msg.TryGetProperty("content", out var content) && content.GetString() is { Length: > 0 } text)
                        yield return new StreamChunk(TextDelta: text);

                    if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in tcs.EnumerateArray())
                        {
                            if (!tc.TryGetProperty("function", out var func) || func.ValueKind != JsonValueKind.Object)
                                continue;
                            var name = func.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            var args = func.TryGetProperty("arguments", out var a)
                                ? (a.ValueKind == JsonValueKind.String ? a.GetString() ?? "{}" : a.GetRawText())
                                : "{}";
                            // Ollama doesn't emit ids for tool calls in /api/chat — synthesize a stable one.
                            // A single chunk carries the fully-realized arguments (no streaming deltas
                            // for tool args in /api/chat), so emit it as one ToolCallDelta the
                            // SessionRuntime will accumulate (and only see once) under this id.
                            var id = $"call_{nextSyntheticToolCallId++}";
                            yield return new StreamChunk(ToolCallDelta: new ToolCallDelta(id, name, args));
                        }
                    }
                }

                if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                {
                    var input = root.TryGetProperty("prompt_eval_count", out var pec) && pec.ValueKind == JsonValueKind.Number ? pec.GetInt32() : 0;
                    var output = root.TryGetProperty("eval_count", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : 0;
                    if (input > 0 || output > 0)
                        yield return new StreamChunk(Usage: new Usage(input, output));

                    // Ollama optionally emits `done_reason` ("stop" | "length" | "load" | …). When
                    // present, map to the normalized StopReason so consumers don't need to know
                    // provider-specific wording.
                    var stopReason = root.TryGetProperty("done_reason", out var dr) && dr.GetString() is { Length: > 0 } drs
                        ? drs switch
                        {
                            "stop" => StopReason.EndTurn,
                            "length" => StopReason.MaxTokens,
                            _ => StopReason.Unknown,
                        }
                        : StopReason.Unknown;
                    yield return new StreamChunk(IsComplete: true, StopReason: stopReason);
                    sawComplete = true;
                }
            }
        }

        if (!sawComplete)
            yield return new StreamChunk(IsComplete: true);
    }

    private string BuildRequestBody(ProviderRequest request)
    {
        using var ms = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();
        writer.WriteString("model", _model);
        writer.WriteBoolean("stream", true);

        writer.WritePropertyName("messages");
        writer.WriteStartArray();
        foreach (var m in request.Messages)
        {
            writer.WriteStartObject();
            writer.WriteString("role", m.Role switch
            {
                MessageRole.System => "system",
                MessageRole.User => "user",
                MessageRole.Assistant => "assistant",
                MessageRole.Tool => "tool",
                _ => "user"
            });
            writer.WriteString("content", m.Content ?? string.Empty);

            if (m.Role == MessageRole.Assistant && m.ToolCalls is { Count: > 0 } calls)
            {
                writer.WritePropertyName("tool_calls");
                writer.WriteStartArray();
                foreach (var tc in calls)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("function");
                    writer.WriteStartObject();
                    writer.WriteString("name", tc.Name);
                    writer.WritePropertyName("arguments");
                    WriteArgumentsObject(writer, tc.ArgumentsJson);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            if (m.Role == MessageRole.Tool && m.ToolCallId is { Length: > 0 } toolCallId)
                writer.WriteString("tool_call_id", toolCallId);

            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        if (request.Tools.Count > 0)
        {
            writer.WritePropertyName("tools");
            writer.WriteStartArray();
            foreach (var tool in request.Tools)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "function");
                writer.WritePropertyName("function");
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                writer.WriteString("description", tool.Description ?? string.Empty);
                writer.WritePropertyName("parameters");
                tool.InputSchema.RootElement.WriteTo(writer);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        if (_numCtx is not null || _temperature is not null)
        {
            writer.WritePropertyName("options");
            writer.WriteStartObject();
            if (_numCtx is int ctx) writer.WriteNumber("num_ctx", ctx);
            if (_temperature is double t) writer.WriteNumber("temperature", t);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteArgumentsObject(Utf8JsonWriter writer, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            doc.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            // Malformed args from a prior turn shouldn't crash a request; emit an empty object so
            // the model sees the call but the malformed payload is dropped.
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsClient) _httpClient.Dispose();
    }
}
