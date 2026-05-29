using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeAgent.Kernel;

/// <summary>
/// Adapts a remote MCP tool into FreeAgent's <see cref="ITool"/> surface so it goes through the
/// same pipeline / permissions as a built-in tool. Names are namespaced as
/// <c>mcp__{serverName}__{toolName}</c> so they can't collide with native tools. Not read-only and
/// not concurrency-safe (the kernel cannot know what an arbitrary remote tool does); each call
/// requires a <see cref="ProcessExecCap"/> for the MCP server's name, so a user can deny a whole
/// server or rule-allow it via permission config.
/// </summary>
public sealed class McpToolAdapter : ITool
{
    private readonly McpClient _client;
    private readonly string _serverName;
    private readonly string _remoteName;

    public McpToolAdapter(McpClient client, string serverName, McpToolInfo info)
    {
        _client = client;
        _serverName = serverName;
        _remoteName = info.Name;
        Name = $"mcp__{serverName}__{info.Name}";
        Description = string.IsNullOrWhiteSpace(info.Description)
            ? $"Tool '{info.Name}' from MCP server '{serverName}'."
            : info.Description;
        InputSchema = JsonDocument.Parse(string.IsNullOrWhiteSpace(info.InputSchemaJson) ? "{}" : info.InputSchemaJson);
    }

    public string Name { get; }
    public string Description { get; }
    public JsonDocument InputSchema { get; }
    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public IReadOnlyList<Capability> RequiredCapabilities(JsonDocument arguments, ToolContext context) =>
        // Treat the MCP server as a process to be approved as a whole.
        [new ProcessExecCap($"mcp:{_serverName}", [_remoteName])];

    public async ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            var call = await _client.CallToolAsync(_remoteName, arguments.RootElement.GetRawText(), cancellationToken);
            if (call.IsError)
            {
                return ToolResult.Error(
                    string.IsNullOrWhiteSpace(call.Text)
                        ? $"MCP tool '{Name}' reported an error."
                        : $"MCP tool '{Name}' reported an error: {call.Text}");
            }

            return string.IsNullOrWhiteSpace(call.Text)
                ? ToolResult.Empty($"MCP tool '{Name}' produced no output.")
                : ToolResult.Success(call.Text);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ToolResult.Crash(
                $"MCP tool '{Name}' failed: {ex.Message}",
                retryHint: "The MCP server returned an error or disconnected. Check its logs.");
        }
    }
}

/// <summary>
/// Configuration entry for a single MCP server (matches the schema in
/// <c>.freeagent/config.json</c> under <c>mcp.servers</c>).
/// </summary>
public sealed record McpServerSpec(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("args")] IReadOnlyList<string>? Args = null,
    [property: JsonPropertyName("env")] IReadOnlyDictionary<string, string>? Env = null);

/// <summary>Project-level MCP configuration: the list of servers to spawn at startup.</summary>
public sealed record McpConfig(
    [property: JsonPropertyName("servers")] IReadOnlyList<McpServerSpec>? Servers = null);
