using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace FreeAgent.Kernel;

/// <summary>
/// Native Anthropic Messages-API streaming provider. Targets <c>POST {baseUrl}/v1/messages</c> with
/// <c>x-api-key</c> + <c>anthropic-version</c> headers (no Bearer). Maps FreeAgent's flat
/// <see cref="Message"/> list into Anthropic's content-block format — <see cref="MessageRole.System"/>
/// messages become the top-level <c>system</c> param; consecutive <see cref="MessageRole.Tool"/>
/// results are merged into a single user message with multiple <c>tool_result</c> blocks (Anthropic
/// requires strict role alternation). SSE events are dispatched on each chunk's <c>type</c> field
/// and normalized to <see cref="StreamChunk"/> (text/thinking/tool-call deltas, usage). Like
/// <see cref="OpenAIProvider"/>, the body is built with <see cref="Utf8JsonWriter"/> (no JSON
/// dependency) and the response is read with <see cref="HttpCompletionOption.ResponseHeadersRead"/>
/// so the SSE body streams incrementally.
/// </summary>
public sealed class AnthropicProvider : IProvider, IDisposable
{
    /// <summary>Anthropic requires <c>max_tokens</c>; this is the default when callers don't override it.</summary>
    public const int DefaultMaxTokens = 4096;

    /// <summary>
    /// Headroom required between <c>max_tokens</c> and <c>thinking.budget_tokens</c> when extended
    /// thinking is enabled. Anthropic rejects requests where the visible reply budget can't even fit
    /// alongside the reasoning trace — auto-bump <c>max_tokens</c> by this much so callers don't have
    /// to remember the constraint.
    /// </summary>
    private const int ThinkingMaxTokensHeadroom = 1024;

    private readonly string _model;
    private readonly int _maxTokens;
    private readonly int _thinkingBudgetTokens;
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly bool _ownsClient;
    private bool _disposed;

    public AnthropicProvider(
        string baseUrl, string apiKey, string model = "claude-3-7-sonnet-latest",
        int maxTokens = DefaultMaxTokens, int thinkingBudgetTokens = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        _model = model;
        _maxTokens = maxTokens;
        _thinkingBudgetTokens = thinkingBudgetTokens < 0 ? 0 : thinkingBudgetTokens;
        _ownsClient = true;
        _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
    }

    public AnthropicProvider(
        HttpClient httpClient, string baseUrl, string model = "claude-3-7-sonnet-latest",
        int maxTokens = DefaultMaxTokens, int thinkingBudgetTokens = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _model = model;
        _maxTokens = maxTokens;
        _thinkingBudgetTokens = thinkingBudgetTokens < 0 ? 0 : thinkingBudgetTokens;
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
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "v1/messages"))
        {
            Content = httpContent
        };
        var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Anthropic API returned {(int)response.StatusCode} ({response.StatusCode}): {error}",
                inner: null,
                statusCode: response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // Per content_block index → tool-use id/name (set at content_block_start; used by input_json_delta).
        var toolUseByIndex = new Dictionary<int, (string Id, string Name)>();
        // stop_reason arrives ahead of message_stop; latch it so we can attach it to the final chunk.
        var finalStopReason = StopReason.Unknown;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line[5..].TrimStart();
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
                continue;

            switch (typeProp.GetString())
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var startMessage)
                        && startMessage.TryGetProperty("usage", out var startUsage)
                        && TryParseUsage(startUsage) is { } u1)
                    {
                        yield return new StreamChunk(Usage: u1);
                    }
                    break;

                case "content_block_start":
                    if (root.TryGetProperty("content_block", out var cb)
                        && cb.TryGetProperty("type", out var cbType) && cbType.GetString() == "tool_use"
                        && root.TryGetProperty("index", out var cbIdx) && cbIdx.ValueKind == JsonValueKind.Number
                        && cb.TryGetProperty("id", out var cbId) && cbId.GetString() is { Length: > 0 } id
                        && cb.TryGetProperty("name", out var cbName) && cbName.GetString() is { Length: > 0 } name)
                    {
                        toolUseByIndex[cbIdx.GetInt32()] = (id, name);
                        yield return new StreamChunk(ToolCallDelta: new ToolCallDelta(id, name, string.Empty));
                    }
                    break;

                case "content_block_delta":
                    if (root.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("type", out var deltaTypeProp))
                    {
                        switch (deltaTypeProp.GetString())
                        {
                            case "text_delta":
                                if (delta.TryGetProperty("text", out var t) && t.GetString() is { Length: > 0 } text)
                                    yield return new StreamChunk(TextDelta: text);
                                break;
                            case "thinking_delta":
                                if (delta.TryGetProperty("thinking", out var th) && th.GetString() is { Length: > 0 } thinking)
                                    yield return new StreamChunk(ThinkingDelta: thinking);
                                break;
                            case "input_json_delta":
                                if (delta.TryGetProperty("partial_json", out var pj)
                                    && pj.GetString() is { } argsChunk
                                    && root.TryGetProperty("index", out var dIdx) && dIdx.ValueKind == JsonValueKind.Number
                                    && toolUseByIndex.TryGetValue(dIdx.GetInt32(), out var tu))
                                {
                                    yield return new StreamChunk(ToolCallDelta: new ToolCallDelta(tu.Id, tu.Name, argsChunk));
                                }
                                break;
                        }
                    }
                    break;

                case "message_delta":
                    if (root.TryGetProperty("usage", out var deltaUsage) && TryParseUsage(deltaUsage) is { } u2)
                    {
                        yield return new StreamChunk(Usage: u2);
                    }
                    // Anthropic emits stop_reason on message_delta, ahead of the message_stop event.
                    if (root.TryGetProperty("delta", out var msgDelta)
                        && msgDelta.TryGetProperty("stop_reason", out var srProp)
                        && srProp.GetString() is { Length: > 0 } sr)
                    {
                        finalStopReason = sr switch
                        {
                            "end_turn" => StopReason.EndTurn,
                            "tool_use" => StopReason.ToolUse,
                            "max_tokens" => StopReason.MaxTokens,
                            "stop_sequence" => StopReason.StopSequence,
                            "refusal" => StopReason.Refusal,
                            _ => StopReason.Unknown,
                        };
                    }
                    break;

                case "message_stop":
                    yield return new StreamChunk(IsComplete: true, StopReason: finalStopReason);
                    break;

                // Ignore "content_block_stop", "ping", and any future event types.
            }
        }

        // Final sentinel in case the stream ended without a message_stop.
        yield return new StreamChunk(IsComplete: true, StopReason: finalStopReason);
    }

    private string BuildRequestBody(ProviderRequest request)
    {
        using var ms = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();
        writer.WriteString("model", _model);

        // When extended thinking is enabled, Anthropic requires max_tokens > budget_tokens. Auto-bump
        // by a fixed headroom so the user only has to set one knob (the budget) and forgets the
        // constraint.
        var effectiveMaxTokens = _thinkingBudgetTokens > 0
            ? Math.Max(_maxTokens, _thinkingBudgetTokens + ThinkingMaxTokensHeadroom)
            : _maxTokens;
        writer.WriteNumber("max_tokens", effectiveMaxTokens);

        if (_thinkingBudgetTokens > 0)
        {
            writer.WritePropertyName("thinking");
            writer.WriteStartObject();
            writer.WriteString("type", "enabled");
            writer.WriteNumber("budget_tokens", _thinkingBudgetTokens);
            writer.WriteEndObject();
        }

        writer.WriteBoolean("stream", true);

        // System messages → top-level "system" param (concatenated).
        var system = string.Join("\n\n", request.Messages
            .Where(m => m.Role == MessageRole.System)
            .Select(m => m.Content)
            .Where(s => !string.IsNullOrEmpty(s)));
        if (system.Length > 0)
            writer.WriteString("system", system);

        // Messages with tool_result merging.
        writer.WritePropertyName("messages");
        writer.WriteStartArray();
        WriteMessages(writer, request.Messages);
        writer.WriteEndArray();

        if (request.Tools.Count > 0)
        {
            writer.WritePropertyName("tools");
            writer.WriteStartArray();
            foreach (var tool in request.Tools)
                WriteTool(writer, tool);
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteMessages(Utf8JsonWriter writer, IReadOnlyList<Message> messages)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (m.Role == MessageRole.System)
                continue; // hoisted to top-level "system"

            if (m.Role == MessageRole.Tool)
            {
                // Merge this + consecutive Tool messages into a single user message with tool_result blocks.
                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WritePropertyName("content");
                writer.WriteStartArray();
                while (i < messages.Count && messages[i].Role == MessageRole.Tool)
                {
                    var result = messages[i];
                    writer.WriteStartObject();
                    writer.WriteString("type", "tool_result");
                    writer.WriteString("tool_use_id", result.ToolCallId ?? string.Empty);
                    writer.WriteString("content", result.Content ?? string.Empty);
                    writer.WriteEndObject();
                    i++;
                }
                i--; // step back so the outer loop's i++ lands on the first non-Tool message
                writer.WriteEndArray();
                writer.WriteEndObject();
                continue;
            }

            // User or Assistant.
            writer.WriteStartObject();
            writer.WriteString("role", m.Role == MessageRole.User ? "user" : "assistant");
            writer.WritePropertyName("content");
            writer.WriteStartArray();

            if (!string.IsNullOrEmpty(m.Content))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", m.Content);
                writer.WriteEndObject();
            }

            if (m.Role == MessageRole.Assistant && m.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in m.ToolCalls)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "tool_use");
                    writer.WriteString("id", tc.Id);
                    writer.WriteString("name", tc.Name);
                    writer.WritePropertyName("input");
                    WriteJsonInput(writer, tc.ArgumentsJson);
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }

    /// <summary>Writes <paramref name="argumentsJson"/> as a JSON value; an empty/invalid string becomes <c>{}</c>.</summary>
    private static void WriteJsonInput(Utf8JsonWriter writer, string argumentsJson)
    {
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                using var args = JsonDocument.Parse(argumentsJson);
                args.RootElement.WriteTo(writer);
                return;
            }
            catch (JsonException)
            {
                // fall through to empty object
            }
        }

        writer.WriteStartObject();
        writer.WriteEndObject();
    }

    private static void WriteTool(Utf8JsonWriter writer, ToolDefinition tool)
    {
        writer.WriteStartObject();
        writer.WriteString("name", tool.Name);
        if (!string.IsNullOrEmpty(tool.Description))
            writer.WriteString("description", tool.Description);
        writer.WritePropertyName("input_schema");
        tool.InputSchema.WriteTo(writer);
        writer.WriteEndObject();
    }

    private static Usage? TryParseUsage(JsonElement usage)
    {
        int input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        var any = false;

        if (usage.TryGetProperty("input_tokens", out var it)) { input = it.GetInt32(); any = true; }
        if (usage.TryGetProperty("output_tokens", out var ot)) { output = ot.GetInt32(); any = true; }
        if (usage.TryGetProperty("cache_read_input_tokens", out var cr)) { cacheRead = cr.GetInt32(); any = true; }
        if (usage.TryGetProperty("cache_creation_input_tokens", out var cw)) { cacheWrite = cw.GetInt32(); any = true; }

        return any ? new Usage(input, output, cacheRead, cacheWrite) : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_ownsClient) _httpClient.Dispose();
        _disposed = true;
    }
}
