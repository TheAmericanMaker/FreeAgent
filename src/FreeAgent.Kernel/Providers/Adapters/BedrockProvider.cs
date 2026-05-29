using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;

namespace FreeAgent.Kernel;

/// <summary>
/// AWS Bedrock provider for Anthropic Claude models hosted on Bedrock. Built on the official
/// <c>AWSSDK.BedrockRuntime</c> SDK so SigV4 signing, region routing, retries, and AWS event-stream
/// (vnd.amazon.eventstream) parsing are handled by Amazon's code — the adapter just translates
/// between FreeAgent's <see cref="ProviderRequest"/> shape and Bedrock's anthropic-on-bedrock
/// payload, and maps the streaming JSON chunks back to <see cref="StreamChunk"/>. Identical wire
/// payload to the direct Anthropic API (see <see cref="AnthropicProvider"/>), with two diffs:
/// <list type="bullet">
///   <item><c>model</c> is in the URL (the SDK's <c>ModelId</c>), not in the body.</item>
///   <item>The body carries <c>anthropic_version</c> instead.</item>
/// </list>
/// Auth comes from the default AWS credential chain — env vars (<c>AWS_ACCESS_KEY_ID</c> +
/// <c>AWS_SECRET_ACCESS_KEY</c>, optionally <c>AWS_SESSION_TOKEN</c>), shared profile, EC2/ECS
/// metadata, or SSO. No FreeAgent-specific credential code.
/// </summary>
public sealed class BedrockProvider : IProvider, IDisposable
{
    /// <summary>Default to the latest Claude 3.7 Sonnet on Bedrock. Region drives availability.</summary>
    public const string DefaultModelId = "anthropic.claude-3-7-sonnet-20250219-v1:0";

    /// <summary>Required Bedrock body field for Anthropic models.</summary>
    private const string BedrockAnthropicVersion = "bedrock-2023-05-31";

    private readonly IAmazonBedrockRuntime _client;
    private readonly string _modelId;
    private readonly int _maxTokens;
    private readonly bool _ownsClient;
    private bool _disposed;

    public BedrockProvider(string region, string modelId = DefaultModelId, int maxTokens = AnthropicProvider.DefaultMaxTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        _modelId = modelId;
        _maxTokens = maxTokens;
        _client = new AmazonBedrockRuntimeClient(RegionEndpoint.GetBySystemName(region));
        _ownsClient = true;
    }

    public BedrockProvider(IAmazonBedrockRuntime client, string modelId = DefaultModelId, int maxTokens = AnthropicProvider.DefaultMaxTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _modelId = modelId;
        _maxTokens = maxTokens;
        _ownsClient = false;
    }

    public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
        ProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var body = BuildRequestBody(request);
        var invokeRequest = new InvokeModelWithResponseStreamRequest
        {
            ModelId = _modelId,
            ContentType = "application/json",
            Accept = "application/json",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(body)),
        };

        using var response = await _client.InvokeModelWithResponseStreamAsync(invokeRequest, cancellationToken);

        var toolUseByIndex = new Dictionary<int, (string Id, string Name)>();
        var finalStopReason = StopReason.Unknown;
        var sawComplete = false;

        // The SDK exposes the AWS event stream as IEnumerable<IEventStreamEvent>; each event's
        // Payload is the JSON chunk shape Anthropic publishes for its streaming responses, so the
        // rest of the dispatch is identical to AnthropicProvider's.
        await foreach (var item in response.Body.WithCancellation(cancellationToken))
        {
            if (item is not PayloadPart payload) continue;
            using var doc = JsonDocument.Parse(payload.Bytes);
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
                        yield return new StreamChunk(Usage: u2);
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
                    sawComplete = true;
                    break;
            }
        }

        if (!sawComplete)
            yield return new StreamChunk(IsComplete: true, StopReason: finalStopReason);
    }

    private string BuildRequestBody(ProviderRequest request)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();
        writer.WriteString("anthropic_version", BedrockAnthropicVersion);
        writer.WriteNumber("max_tokens", _maxTokens);

        var system = string.Join("\n\n", request.Messages
            .Where(m => m.Role == MessageRole.System)
            .Select(m => m.Content)
            .Where(s => !string.IsNullOrEmpty(s)));
        if (system.Length > 0)
            writer.WriteString("system", system);

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
            if (m.Role == MessageRole.System) continue;

            if (m.Role == MessageRole.Tool)
            {
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
                i--;
                writer.WriteEndArray();
                writer.WriteEndObject();
                continue;
            }

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
                    if (string.IsNullOrWhiteSpace(tc.ArgumentsJson))
                    {
                        writer.WriteStartObject();
                        writer.WriteEndObject();
                    }
                    else
                    {
                        try
                        {
                            using var argsDoc = JsonDocument.Parse(tc.ArgumentsJson);
                            argsDoc.RootElement.WriteTo(writer);
                        }
                        catch (JsonException)
                        {
                            writer.WriteStartObject();
                            writer.WriteEndObject();
                        }
                    }
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }

    private static void WriteTool(Utf8JsonWriter writer, ToolDefinition tool)
    {
        writer.WriteStartObject();
        writer.WriteString("name", tool.Name);
        writer.WriteString("description", tool.Description ?? string.Empty);
        writer.WritePropertyName("input_schema");
        tool.InputSchema.RootElement.WriteTo(writer);
        writer.WriteEndObject();
    }

    private static Usage? TryParseUsage(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object) return null;
        var input = usage.TryGetProperty("input_tokens", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0;
        var output = usage.TryGetProperty("output_tokens", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : 0;
        return input == 0 && output == 0 ? null : new Usage(input, output);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsClient && _client is IDisposable disposable) disposable.Dispose();
    }
}
