using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace FreeAgent.Kernel;

/// <summary>
/// OpenAI-compatible streaming provider adapter. Supports chat.completions with
/// tool-calling via SSE. Normalises trailing slashes on the base URL. Thread-safe
/// for concurrent calls because <see cref="HttpClient"/> is shared and immutable
/// after construction.
/// </summary>
public sealed class OpenAIProvider : IProvider, IDisposable
{
    private readonly string _model;
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly bool _ownsClient;
    private bool _disposed;

    public OpenAIProvider(string baseUrl, string apiKey, string model = "gpt-4o")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        _model = model;
        _ownsClient = true;
        // No request timeout: a streamed completion can legitimately run for minutes, and the
        // default 100s HttpClient.Timeout would abort it. Per-call cancellation comes from the
        // CancellationToken passed to StreamChatAsync instead.
        _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
    }

    public OpenAIProvider(HttpClient httpClient, string baseUrl, string model = "gpt-4o")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _model = model;
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
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "chat/completions"))
        {
            Content = httpContent
        };
        // ResponseHeadersRead returns as soon as the headers arrive, so the SSE body is read
        // incrementally below. The default (ResponseContentRead) would buffer the entire
        // response before returning, collapsing the stream into a single burst.
        var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"OpenAI API returned {(int)response.StatusCode} ({response.StatusCode}): {error}",
                inner: null,
                statusCode: response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var idByIndex = new Dictionary<int, string>();

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
                continue;
            // The space after "data:" is optional per the SSE spec; accept both "data: {...}"
            // and "data:{...}" and strip a single leading space if present.
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line[5..].TrimStart();
            if (data == "[DONE]")
                break;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            // Usage-only chunk has empty choices array
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                if (root.TryGetProperty("usage", out var usage))
                {
                    var u = TryParseUsage(usage);
                    if (u is not null)
                        yield return new StreamChunk(null, null, null, u, false);
                }
                continue;
            }

            var choice = choices[0];
            bool isComplete = false;

            if (choice.TryGetProperty("finish_reason", out var fr)
                && fr.ValueKind != JsonValueKind.Null
                && !string.IsNullOrEmpty(fr.GetString()))
            {
                isComplete = true;
            }

            if (choice.TryGetProperty("delta", out var delta))
            {
                // Reasoning/thinking delta. Reasoning models on OpenAI-compatible endpoints stream
                // their chain-of-thought separately from the answer: OpenAI-style "reasoning" and
                // DeepSeek-style "reasoning_content" are both accepted here.
                if ((delta.TryGetProperty("reasoning_content", out var reasoningProp)
                        || delta.TryGetProperty("reasoning", out reasoningProp))
                    && reasoningProp.ValueKind == JsonValueKind.String
                    && reasoningProp.GetString() is { Length: > 0 } reasoning)
                {
                    yield return new StreamChunk(reasoning, null, null, null, isComplete);
                }

                // Text delta
                if (delta.TryGetProperty("content", out var contentProp)
                    && contentProp.ValueKind == JsonValueKind.String)
                {
                    var text = contentProp.GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(text))
                    {
                        yield return new StreamChunk(null, text, null, null, isComplete);
                    }
                    else if (isComplete)
                    {
                        yield return new StreamChunk(null, null, null, null, true);
                    }
                }

                // Tool-call deltas
                if (delta.TryGetProperty("tool_calls", out var toolCalls))
                {
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        if (!tc.TryGetProperty("index", out var idxProp))
                            continue;
                        var index = idxProp.GetInt32();

                        string id;
                        if (tc.TryGetProperty("id", out var idProp) && idProp.GetString() is { } parsedId)
                        {
                            id = parsedId;
                            idByIndex[index] = id;
                        }
                        else if (idByIndex.TryGetValue(index, out var cachedId))
                        {
                            id = cachedId;
                        }
                        else
                        {
                            continue;
                        }

                        string name = string.Empty;
                        string args = string.Empty;

                        if (tc.TryGetProperty("function", out var func))
                        {
                            if (func.TryGetProperty("name", out var nameProp) && nameProp.GetString() is { } n)
                                name = n;
                            if (func.TryGetProperty("arguments", out var argsProp) && argsProp.ValueKind == JsonValueKind.String)
                                args = argsProp.GetString() ?? string.Empty;
                        }

                        yield return new StreamChunk(null, null, new ToolCallDelta(id, name, args), null, isComplete);
                    }
                }
            }
            else if (isComplete)
            {
                // finish_reason set but no delta (final sentinel)
                yield return new StreamChunk(null, null, null, null, true);
            }
        }

        // Stream ended naturally or via [DONE]
        yield return new StreamChunk(null, null, null, null, true);
    }

    private string BuildRequestBody(ProviderRequest request)
    {
        using var ms = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();
        writer.WriteString("model", _model);

        writer.WritePropertyName("messages");
        writer.WriteStartArray();
        foreach (var message in request.Messages)
            WriteMessage(writer, message);
        writer.WriteEndArray();

        // Omit "tools" entirely when there are none: OpenAI rejects an empty "tools": [] with HTTP 400.
        if (request.Tools.Count > 0)
        {
            writer.WritePropertyName("tools");
            writer.WriteStartArray();
            foreach (var tool in request.Tools)
                WriteTool(writer, tool);
            writer.WriteEndArray();
        }

        writer.WriteBoolean("stream", true);
        writer.WritePropertyName("stream_options");
        writer.WriteStartObject();
        writer.WriteBoolean("include_usage", true);
        writer.WriteEndObject();

        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteMessage(Utf8JsonWriter writer, Message message)
    {
        writer.WriteStartObject();

        writer.WriteString("role", message.Role switch
        {
            MessageRole.System => "system",
            MessageRole.User => "user",
            MessageRole.Assistant => "assistant",
            MessageRole.Tool => "tool",
            _ => "user"
        });

        if (message.Role == MessageRole.Tool && !string.IsNullOrEmpty(message.ToolCallId))
            writer.WriteString("tool_call_id", message.ToolCallId);

        if (message.Role == MessageRole.Tool && !string.IsNullOrEmpty(message.ToolName))
            writer.WriteString("name", message.ToolName);

        writer.WriteString("content", message.Content ?? string.Empty);

        if (message.ToolCalls?.Count > 0 && message.Role == MessageRole.Assistant)
        {
            writer.WritePropertyName("tool_calls");
            writer.WriteStartArray();
            foreach (var tc in message.ToolCalls)
            {
                writer.WriteStartObject();
                writer.WriteString("id", tc.Id);
                writer.WriteString("type", "function");
                writer.WritePropertyName("function");
                writer.WriteStartObject();
                writer.WriteString("name", tc.Name);
                writer.WriteString("arguments", tc.ArgumentsJson);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteTool(Utf8JsonWriter writer, ToolDefinition tool)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "function");
        writer.WritePropertyName("function");
        writer.WriteStartObject();
        writer.WriteString("name", tool.Name);
        if (!string.IsNullOrEmpty(tool.Description))
            writer.WriteString("description", tool.Description);
        writer.WritePropertyName("parameters");
        tool.InputSchema.WriteTo(writer);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static Usage? TryParseUsage(JsonElement usage)
    {
        int input = 0;
        int output = 0;
        bool hasAny = false;

        if (usage.TryGetProperty("prompt_tokens", out var pt))
        {
            input = pt.GetInt32();
            hasAny = true;
        }
        else if (usage.TryGetProperty("input_tokens", out var it))
        {
            input = it.GetInt32();
            hasAny = true;
        }

        if (usage.TryGetProperty("completion_tokens", out var ct))
        {
            output = ct.GetInt32();
            hasAny = true;
        }
        else if (usage.TryGetProperty("output_tokens", out var ot))
        {
            output = ot.GetInt32();
            hasAny = true;
        }

        return hasAny ? new Usage(input, output) : null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_ownsClient)
            _httpClient.Dispose();

        _disposed = true;
    }
}
