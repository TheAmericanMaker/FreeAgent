using FreeAgent.Kernel;

namespace FreeAgent.Host;

/// <summary>
/// Spawns every <see cref="LspServerSpec"/> configured under <c>.freeagent/config.json</c>'s
/// <c>lsp.servers[]</c>, runs the LSP <c>initialize</c> handshake, and registers four tools per
/// server — <c>lsp__{name}__hover</c> / <c>__definition</c> / <c>__references</c> / <c>__open</c> —
/// into the parent registry. Mirrors the shape of <see cref="McpServerManager"/>: per-server
/// failures are isolated, the others still come up. Dispose to stop them all and reap their
/// processes.
/// </summary>
public sealed class LspServerManager : IAsyncDisposable
{
    private readonly List<LspClient> _clients = new();
    private readonly List<StdioLspTransport> _transports = new();

    public IReadOnlyList<LspClient> ActiveClients => _clients;

    public async Task StartAsync(IReadOnlyList<LspServerSpec> servers, ToolRegistry registry, string workingDirectory, CancellationToken cancellationToken)
    {
        foreach (var spec in servers)
        {
            StdioLspTransport? transport = null;
            LspClient? client = null;
            try
            {
                transport = new StdioLspTransport(spec.Command, spec.Args, workingDirectory);
                client = new LspClient(transport);
                var rootUri = new Uri(workingDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).AbsoluteUri;
                await client.InitializeAsync(rootUri, cancellationToken).ConfigureAwait(false);

                foreach (var action in (LspToolAdapter.LspAction[])Enum.GetValues(typeof(LspToolAdapter.LspAction)))
                    registry.Register(new LspToolAdapter(client, spec, action));

                _clients.Add(client);
                _transports.Add(transport);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[freeagent] LSP server '{spec.Name}' failed to start: {ex.Message}");
                try { client?.Dispose(); } catch { }
                try { transport?.Dispose(); } catch { }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch { /* swallow shutdown errors */ }
        }
        _clients.Clear();
        foreach (var transport in _transports)
        {
            try { transport.Dispose(); }
            catch { /* swallow */ }
        }
        _transports.Clear();
    }
}
