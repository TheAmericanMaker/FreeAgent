using System.Text;
using System.Text.Json;

namespace FreeAgent.Kernel;

/// <summary>An MCP tool description discovered via <c>tools/list</c>.</summary>
public sealed record McpToolInfo(string Name, string Description, string InputSchemaJson);

/// <summary>
/// MCP (Model Context Protocol) client built on top of <see cref="JsonRpcClient"/>. Owns just the
/// MCP-specific methods (<c>initialize</c>, <c>tools/list</c>, <c>tools/call</c>); transport (stdio,
/// socket, fake) is injected. Limited to text content blocks in tool results for v1.
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ClientName = "freeagent";
    private const string ClientVersion = "0.1.0";

    private readonly JsonRpcClient _rpc;

    public McpClient(IMcpTransport transport) => _rpc = new JsonRpcClient(transport);

    /// <summary>Sends <c>initialize</c> + the required <c>notifications/initialized</c>.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _rpc.CallAsync("initialize", w =>
        {
            w.WriteString("protocolVersion", ProtocolVersion);
            w.WritePropertyName("capabilities");
            w.WriteStartObject();
            w.WriteEndObject();
            w.WritePropertyName("clientInfo");
            w.WriteStartObject();
            w.WriteString("name", ClientName);
            w.WriteString("version", ClientVersion);
            w.WriteEndObject();
        }, cancellationToken);

        await _rpc.NotifyAsync("notifications/initialized", null, cancellationToken);
    }

    /// <summary>Discovers the server's tools via <c>tools/list</c>.</summary>
    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var result = await _rpc.CallAsync("tools/list", null, cancellationToken);
        var list = new List<McpToolInfo>();

        if (!result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            var description = tool.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;
            var schema = tool.TryGetProperty("inputSchema", out var s) ? s.GetRawText() : "{}";
            list.Add(new McpToolInfo(name, description, schema));
        }

        return list;
    }

    /// <summary>
    /// Invokes <c>tools/call</c>. <paramref name="argumentsJson"/> is embedded as the <c>arguments</c>
    /// object directly. Returns the concatenated text from the result's content blocks (any
    /// non-text block types are skipped for v1).
    /// </summary>
    public async Task<string> CallToolAsync(string name, string argumentsJson, CancellationToken cancellationToken)
    {
        var result = await _rpc.CallAsync("tools/call", w =>
        {
            w.WriteString("name", name);
            w.WritePropertyName("arguments");
            if (string.IsNullOrWhiteSpace(argumentsJson))
            {
                w.WriteStartObject();
                w.WriteEndObject();
            }
            else
            {
                using var args = JsonDocument.Parse(argumentsJson);
                args.RootElement.WriteTo(w);
            }
        }, cancellationToken);

        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type) && type.GetString() == "text"
                && block.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                sb.Append(text.GetString());
            }
        }
        return sb.ToString();
    }

    public ValueTask DisposeAsync() => _rpc.DisposeAsync();
}
