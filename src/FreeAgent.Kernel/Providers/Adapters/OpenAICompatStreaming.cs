using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace FreeAgent.Kernel;

/// <summary>
/// Shared OpenAI-style chat-completions request body + SSE streaming parser, used by both
/// <see cref="OpenAIProvider"/> and OpenAI-API-compatible providers (Azure OpenAI, Groq, etc.).
/// Knowing about the wire format lives here so each provider only owns its URL/auth differences.
/// </summary>
internal static class OpenAICompatStreaming
{
    /// <summary>Builds the JSON request body for <c>POST /chat/completions</c>.</summary>
    public static string BuildRequestBody(string model, ProviderRequest request)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();
        writer.WriteString("model", model);

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

    /// <summary>
    /// Reads the SSE response body and yields normalised <see cref="StreamChunk"/>s. Reassembles
    /// tool-call deltas by stable id (mapping the provider's <c>index</c> → first observed <c>id</c>),
    /// emits text and reasoning deltas, and extracts usage (OpenAI <c>prompt_tokens</c>/<c>completion_tokens</c>
    /// or Anthropic-style <c>input_tokens</c>/<c>output_tokens</c> field names).
    /// </summary>
    public static async IAsyncEnumerable<StreamChunk> ReadStreamAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var idByIndex = new Dictionary<int, string>();
        var anyComplete = false; // did any chunk already carry IsComplete? (avoids a duplicate end sentinel)

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
                continue;
            // The space after "data:" is optional per the SSE spec; accept both forms.
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line[5..].TrimStart();
            if (data == "[DONE]")
                break;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch (JsonException) { continue; } // skip a malformed SSE data line; don't abort the stream
            using var ownedDoc = doc;
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
            var stopReason = StopReason.Unknown;

            if (choice.TryGetProperty("finish_reason", out var fr)
                && fr.ValueKind != JsonValueKind.Null
                && fr.GetString() is { Length: > 0 } finishReason)
            {
                isComplete = true;
                anyComplete = true;
                stopReason = finishReason switch
                {
                    "stop" => StopReason.EndTurn,
                    "length" => StopReason.MaxTokens,
                    "tool_calls" or "function_call" => StopReason.ToolUse,
                    "content_filter" => StopReason.Refusal,
                    _ => StopReason.Unknown,
                };
            }

            if (choice.TryGetProperty("delta", out var delta))
            {
                // Reasoning/thinking delta (OpenAI "reasoning", DeepSeek "reasoning_content").
                if ((delta.TryGetProperty("reasoning_content", out var reasoningProp)
                        || delta.TryGetProperty("reasoning", out reasoningProp))
                    && reasoningProp.ValueKind == JsonValueKind.String
                    && reasoningProp.GetString() is { Length: > 0 } reasoning)
                {
                    yield return new StreamChunk(reasoning, null, null, null, isComplete, stopReason);
                }

                // Text delta
                if (delta.TryGetProperty("content", out var contentProp)
                    && contentProp.ValueKind == JsonValueKind.String)
                {
                    var text = contentProp.GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(text))
                    {
                        yield return new StreamChunk(null, text, null, null, isComplete, stopReason);
                    }
                    else if (isComplete)
                    {
                        yield return new StreamChunk(null, null, null, null, true, stopReason);
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

                        yield return new StreamChunk(null, null, new ToolCallDelta(id, name, args), null, isComplete, stopReason);
                    }
                }
            }
            else if (isComplete)
            {
                yield return new StreamChunk(null, null, null, null, true, stopReason);
            }
        }

        // Fallback end-of-stream sentinel — only if the stream never sent a finish_reason chunk, so a
        // well-behaved server doesn't get a redundant second IsComplete chunk after [DONE].
        if (!anyComplete)
            yield return new StreamChunk(null, null, null, null, true);
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

        // OpenAI reports cache hits under prompt_tokens_details.cached_tokens (cache reads only; it
        // has no separate cache-write count). Surface it as the normalized CacheReadTokens.
        var cacheRead = 0;
        if (usage.TryGetProperty("prompt_tokens_details", out var details)
            && details.TryGetProperty("cached_tokens", out var cached)
            && cached.ValueKind == JsonValueKind.Number)
        {
            cacheRead = cached.GetInt32();
        }

        return hasAny ? new Usage(input, output, cacheRead) : null;
    }
}
