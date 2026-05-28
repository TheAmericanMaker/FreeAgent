using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace FreeAgent.Kernel.Tests.Providers.Adapters;

public sealed class VertexProviderTests : IDisposable
{
    private readonly FakeHandler _handler = new();
    private readonly HttpClient _httpClient;
    private readonly StaticTokenSource _tokenSource = new("test-bearer-token");
    private VertexProvider _provider = null!;

    public VertexProviderTests() => _httpClient = new HttpClient(_handler);

    public void Dispose()
    {
        _handler.Dispose();
        _httpClient.Dispose();
        _provider?.Dispose();
    }

    private static ProviderRequest StubRequest()
    {
        var msg = new Message(MessageRole.User, "Hello");
        var tool = new ToolDefinition("read_file", "Read a file.", JsonDocument.Parse("{\"type\":\"object\"}"), true, true);
        return new ProviderRequest([msg], [tool]);
    }

    private VertexProvider NewProvider() =>
        new("my-proj", "us-central1", httpClient: _httpClient, tokenSource: _tokenSource);

    [Fact]
    public async Task UrlEmbedsProjectLocationAndModel()
    {
        _handler.RespondWith("data: {\"type\":\"message_stop\"}\n\n");
        _provider = new VertexProvider("my-proj", "us-central1", modelId: "claude-3-7-sonnet@20250219",
            httpClient: _httpClient, tokenSource: _tokenSource);

        await _provider.StreamChatAsync(StubRequest(), default).ToListAsync();

        _handler.LastRequestUri!.ToString().Should().Be(
            "https://us-central1-aiplatform.googleapis.com/v1/projects/my-proj/locations/us-central1/publishers/anthropic/models/claude-3-7-sonnet@20250219:streamRawPredict");
    }

    [Fact]
    public async Task SendsBearerTokenFromTokenSource()
    {
        _handler.RespondWith("data: {\"type\":\"message_stop\"}\n\n");
        _provider = NewProvider();

        await _provider.StreamChatAsync(StubRequest(), default).ToListAsync();

        _handler.LastAuthHeader.Should().Be("Bearer test-bearer-token");
    }

    [Fact]
    public async Task BodyCarriesVertexAnthropicVersionAndOmitsTopLevelModel()
    {
        _handler.RespondWith("data: {\"type\":\"message_stop\"}\n\n");
        _provider = NewProvider();

        await _provider.StreamChatAsync(StubRequest(), default).ToListAsync();

        _handler.LastBody.Should().Contain("\"anthropic_version\":\"vertex-2023-10-16\"");
        _handler.LastBody.Should().Contain("\"max_tokens\":");
        _handler.LastBody.Should().Contain("\"stream\":true");
        // model id is in the URL, not the body
        _handler.LastBody.Should().NotContain("\"model\":");
    }

    [Fact]
    public async Task SystemMessagesHoistedToTopLevelSystem()
    {
        _handler.RespondWith("data: {\"type\":\"message_stop\"}\n\n");
        _provider = NewProvider();

        var req = new ProviderRequest(
            [new Message(MessageRole.System, "Be concise."), new Message(MessageRole.User, "hi")], []);
        await _provider.StreamChatAsync(req, default).ToListAsync();

        _handler.LastBody.Should().Contain("\"system\":\"Be concise.\"");
        _handler.LastBody.Should().NotContain("\"role\":\"system\"");
    }

    [Fact]
    public async Task TextDeltasStreamThrough()
    {
        _handler.RespondWith(
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":7}}}\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\" there\"}}\n" +
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":3}}\n" +
            "data: {\"type\":\"message_stop\"}\n\n");
        _provider = NewProvider();

        var chunks = await _provider.StreamChatAsync(StubRequest(), default).ToListAsync();

        chunks.Where(c => c.TextDelta is not null).Select(c => c.TextDelta).Should().Equal("hi", " there");
        chunks.Should().Contain(c => c.IsComplete && c.StopReason == StopReason.EndTurn);
    }

    [Fact]
    public async Task NonSuccessResponseThrowsWithBody()
    {
        _handler.RespondWith("project not found", HttpStatusCode.NotFound);
        _provider = NewProvider();

        var act = async () => await _provider.StreamChatAsync(StubRequest(), default).ToListAsync();

        await act.Should().ThrowAsync<HttpRequestException>()
            .Where(e => e.Message.Contains("404") && e.Message.Contains("project not found"));
    }

    [Fact]
    public void ConstructorRejectsBlankProjectOrLocation()
    {
        Action a = () => new VertexProvider("", "us-central1", tokenSource: _tokenSource);
        Action b = () => new VertexProvider("proj", "", tokenSource: _tokenSource);
        a.Should().Throw<ArgumentException>();
        b.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DefaultModelIdIsClaude37Sonnet()
    {
        VertexProvider.DefaultModelId.Should().Be("claude-3-7-sonnet@20250219");
    }

    private sealed class StaticTokenSource(string token) : VertexProvider.ITokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult(token);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private string _payload = "";
        private HttpStatusCode _status = HttpStatusCode.OK;
        public string? LastBody { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public string? LastAuthHeader { get; private set; }

        public void RespondWith(string payload, HttpStatusCode status = HttpStatusCode.OK)
        {
            _payload = payload;
            _status = status;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastAuthHeader = request.Headers.Authorization?.ToString();
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

            var content = new StringContent(_payload, System.Text.Encoding.UTF8, "text/event-stream");
            return new HttpResponseMessage(_status) { Content = content, RequestMessage = request };
        }
    }
}
