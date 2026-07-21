using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Lsp;

/// <summary>
/// End-to-end smoke test for <see cref="LspClient"/> over an in-memory transport. Lives in
/// <see cref="JsonRpcCollection"/> so it runs sequentially with the MCP smoke test — both wrap a
/// <c>JsonRpcClient</c> with a background read loop, and the runner-parallelism interaction with
/// disposal of those loops was the original hang.
/// </summary>
[Collection(JsonRpcCollection.Name)]
public sealed class LspClientTests
{
    private sealed class FakeLspTransport : ILspTransport
    {
        private readonly Channel<string> _serverToClient = Channel.CreateUnbounded<string>();
        public List<string> Written { get; } = new();

        public void EnqueueResponse(string envelope) => _serverToClient.Writer.TryWrite(envelope);

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
        var transport = new FakeLspTransport();
        await using var client = new LspClient(transport);

        // initialize (id=1)
        transport.EnqueueResponse(ResultEnvelope(1, "{\"capabilities\":{}}"));
        await client.InitializeAsync("file:///workspace/", CancellationToken.None);

        transport.Written.Should().HaveCount(2);
        transport.Written[0].Should().Contain("\"method\":\"initialize\"").And.Contain("\"rootUri\":\"file:///workspace/\"");
        transport.Written[1].Should().Contain("\"method\":\"initialized\"");

        // hover (id=2)
        transport.EnqueueResponse(ResultEnvelope(2, """{"contents":{"kind":"markdown","value":"`int Foo(int x)`"}}"""));
        var hover = await client.HoverAsync("file:///a.cs", 3, 7, CancellationToken.None);
        hover.Should().Be("`int Foo(int x)`");

        // definition (id=3) — single Location
        transport.EnqueueResponse(ResultEnvelope(3, """{"uri":"file:///b.cs","range":{"start":{"line":10,"character":4},"end":{"line":10,"character":8}}}"""));
        var defs = await client.DefinitionAsync("file:///a.cs", 3, 7, CancellationToken.None);
        defs.Should().ContainSingle().Which.Should().Be("file:///b.cs:11:5");

        // references (id=4) — array of locations
        transport.EnqueueResponse(ResultEnvelope(4, """
            [{"uri":"file:///c.cs","range":{"start":{"line":1,"character":0},"end":{"line":1,"character":3}}},
             {"uri":"file:///d.cs","range":{"start":{"line":4,"character":2},"end":{"line":4,"character":5}}}]
            """));
        var refs = await client.ReferencesAsync("file:///a.cs", 3, 7, includeDeclaration: false, CancellationToken.None);
        refs.Should().Equal("file:///c.cs:2:1", "file:///d.cs:5:3");
        // Written sequence: initialize, initialized (notification), hover, definition, references.
        transport.Written[4].Should().Contain("\"includeDeclaration\":false");
    }
}
