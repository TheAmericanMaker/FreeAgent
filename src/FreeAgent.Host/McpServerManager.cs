using FreeAgent.Kernel;

namespace FreeAgent.Host;

/// <summary>
/// Spawns each configured MCP server, runs the initialize handshake + <c>tools/list</c>, registers
/// each discovered tool into the parent <see cref="IToolRegistry"/> via <see cref="McpToolAdapter"/>,
/// and keeps the <see cref="McpClient"/>s alive for the lifetime of the host. Failures are isolated
/// per server — one bad server doesn't take the whole host down.
/// </summary>
public sealed class McpServerManager : IAsyncDisposable
{
    private readonly List<McpClient> _clients = new();

    public int ServerCount => _clients.Count;

    public IReadOnlyList<string> ServerNames { get; private set; } = Array.Empty<string>();

    public async Task StartAsync(IReadOnlyList<McpServerSpec> servers, IToolRegistry registry, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        foreach (var spec in servers)
        {
            try
            {
                var transport = new StdioMcpTransport(spec.Command, spec.Args, spec.Env);
                var client = new McpClient(transport);
                await client.InitializeAsync(cancellationToken);
                var tools = await client.ListToolsAsync(cancellationToken);
                foreach (var tool in tools)
                    registry.Register(new McpToolAdapter(client, spec.Name, tool));
                _clients.Add(client);
                names.Add(spec.Name);
                Console.WriteLine($"MCP: connected to '{spec.Name}' ({tools.Count} tool(s))");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"MCP: server '{spec.Name}' failed to start: {ex.Message}");
            }
        }
        ServerNames = names;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            try { await client.DisposeAsync(); } catch { /* swallow during teardown */ }
        _clients.Clear();
    }
}
