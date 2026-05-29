using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;

namespace FreeAgent.Kernel;

/// <summary>
/// Google Vertex AI provider for Anthropic Claude models hosted on Vertex. Same Messages-API body
/// shape as the direct Anthropic provider (with two diffs: <c>anthropic_version</c> is
/// <c>vertex-2023-10-16</c>, and <c>model</c> is in the URL not the body), and the SSE stream
/// shape is identical so chunk dispatch reuses the Anthropic logic.
/// <para>
/// Auth uses <see cref="GoogleCredential"/> Application Default Credentials:
/// <c>GOOGLE_APPLICATION_CREDENTIALS</c> env var, gcloud auth login, GCE metadata, etc. — same
/// credential chain every gcloud-aware tool uses. No FreeAgent-specific credential code.
/// </para>
/// </summary>
public sealed class VertexProvider : IProvider, IDisposable
{
    private const string VertexAnthropicVersion = "vertex-2023-10-16";
    private const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";

    /// <summary>Default to Claude 3.7 Sonnet on Vertex. Region must match where the model is enabled.</summary>
    public const string DefaultModelId = "claude-3-7-sonnet@20250219";

    private readonly string _projectId;
    private readonly string _location;
    private readonly string _modelId;
    private readonly int _maxTokens;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly ITokenSource _tokenSource;
    private bool _disposed;

    public VertexProvider(
        string projectId,
        string location,
        string modelId = DefaultModelId,
        int maxTokens = AnthropicProvider.DefaultMaxTokens,
        HttpClient? httpClient = null,
        ITokenSource? tokenSource = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        _projectId = projectId;
        _location = location;
        _modelId = modelId;
        _maxTokens = maxTokens;
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _ownsClient = httpClient is null;
        _tokenSource = tokenSource ?? new ApplicationDefaultTokenSource();
    }

    public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
        ProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var token = await _tokenSource.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var url = $"https://{_location}-aiplatform.googleapis.com/v1/projects/{_projectId}/locations/{_location}/publishers/anthropic/models/{_modelId}:streamRawPredict";
        var body = BuildRequestBody(request);

        using var httpContent = new StringContent(body, Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = httpContent };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Vertex API returned {(int)response.StatusCode} ({response.StatusCode}): {err}",
                inner: null,
                statusCode: response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var toolUseByIndex = new Dictionary<int, (string Id, string Name)>();
        var finalStopReason = StopReason.Unknown;
        var sawComplete = false;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line[5..].TrimStart();
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String) continue;

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
                    if (root.TryGetProperty("delta", out var delta) && delta.TryGetProperty("type", out var dt))
                    {
                        switch (dt.GetString())
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
        writer.WriteString("anthropic_version", VertexAnthropicVersion);
        writer.WriteNumber("max_tokens", _maxTokens);
        writer.WriteBoolean("stream", true);

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
                    var r = messages[i];
                    writer.WriteStartObject();
                    writer.WriteString("type", "tool_result");
                    writer.WriteString("tool_use_id", r.ToolCallId ?? string.Empty);
                    writer.WriteString("content", r.Content ?? string.Empty);
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
                        try { using var argsDoc = JsonDocument.Parse(tc.ArgumentsJson); argsDoc.RootElement.WriteTo(writer); }
                        catch (JsonException) { writer.WriteStartObject(); writer.WriteEndObject(); }
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
        if (_ownsClient) _httpClient.Dispose();
    }

    /// <summary>Seam for the bearer token. Lets tests inject a fixed token without going through ADC.</summary>
    public interface ITokenSource
    {
        Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
    }

    private sealed class ApplicationDefaultTokenSource : ITokenSource
    {
        public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            var credential = await GoogleCredential.GetApplicationDefaultAsync(cancellationToken).ConfigureAwait(false);
            credential = credential.CreateScoped(CloudPlatformScope);
            return await credential.UnderlyingCredential.GetAccessTokenForRequestAsync(authUri: null, cancellationToken).ConfigureAwait(false);
        }
    }
}
