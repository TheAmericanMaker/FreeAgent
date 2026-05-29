using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Mcp;

/// <summary>
/// End-to-end smoke test for the MCP client over an in-memory transport. Covers initialize,
/// tools/list parsing, tools/call argument embedding, and JSON-RPC error surfacing in one
/// `[Fact]`. Lives in <see cref="JsonRpcCollection"/> so it runs sequentially with the LSP smoke
/// test — running them in parallel hits a race between one client's background read-loop disposal
/// and the next client's start.
/// </summary>
[Collection(JsonRpcCollection.Name)]
public sealed class McpClientTests
{
    private sealed class FakeTransport : IMcpTransport
    {
        private readonly Channel<string> _serverToClient = Channel.CreateUnbounded<string>();
        public List<string> Written { get; } = new();

        public void EnqueueResponse(string line) => _serverToClient.Writer.TryWrite(line);

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try { return await _serverToClient.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) { return null; }
        }

        public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            Written.Add(line);
            return ValueTask.CompletedTask;
        }

        public void Dispose() => _serverToClient.Writer.TryComplete();
    }

    private static string ResultEnvelope(int id, string resultJson) =>
        $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{resultJson}}}";

    [Fact]
    public async Task EndToEndProtocolFlow()
    {
        var transport = new FakeTransport();
        await using var client = new McpClient(transport);

        // initialize (id=1) + the required notifications/initialized
        transport.EnqueueResponse(ResultEnvelope(1, "{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{}}"));
        await client.InitializeAsync(CancellationToken.None);

        transport.Written.Should().HaveCount(2);
        transport.Written[0].Should().Contain("\"method\":\"initialize\"")
            .And.Contain("\"protocolVersion\":\"2024-11-05\"");
        transport.Written[1].Should().Contain("\"method\":\"notifications/initialized\"");

        // tools/list (id=2) — parse multi-tool result
        transport.EnqueueResponse(ResultEnvelope(2, """
            {"tools":[
              {"name":"hello","description":"Say hi","inputSchema":{"type":"object","properties":{"name":{"type":"string"}}}},
              {"name":"echo","description":"Echo input","inputSchema":{"type":"object"}}
            ]}
            """));
        var tools = await client.ListToolsAsync(CancellationToken.None);

        tools.Should().HaveCount(2);
        tools[0].Name.Should().Be("hello");
        tools[0].Description.Should().Be("Say hi");
        tools[0].InputSchemaJson.Should().Contain("\"properties\"");
        tools[1].Name.Should().Be("echo");

        // tools/call (id=3) — embed arguments, concatenate text content blocks, skip non-text
        transport.EnqueueResponse(ResultEnvelope(3, """
            {"content":[
              {"type":"text","text":"hello "},
              {"type":"text","text":"world"},
              {"type":"image","data":"…"}
            ],"isError":false}
            """));
        var call = await client.CallToolAsync("greet", "{\"name\":\"Alice\"}", CancellationToken.None);

        call.Text.Should().Be("hello world");
        call.IsError.Should().BeFalse();
        // Written sequence: initialize, notifications/initialized, tools/list, tools/call.
        transport.Written[3].Should().Contain("\"method\":\"tools/call\"")
            .And.Contain("\"name\":\"greet\"")
            .And.Contain("\"name\":\"Alice\"");

        // tools/call (id=4) — a server-reported failure surfaces via IsError instead of a silent success.
        transport.EnqueueResponse(ResultEnvelope(4, """
            {"content":[{"type":"text","text":"boom"}],"isError":true}
            """));
        var failed = await client.CallToolAsync("greet", "{}", CancellationToken.None);

        failed.IsError.Should().BeTrue();
        failed.Text.Should().Be("boom");
    }
}
