using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace FreeAgent.Kernel.Tests.Providers.Adapters;

public sealed class AzureOpenAIProviderTests : IDisposable
{
    private readonly FakeHandler _handler = new();
    private readonly HttpClient _httpClient;
    private AzureOpenAIProvider _provider = null!;

    public AzureOpenAIProviderTests()
    {
        _httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://fake.local/") };
    }

    public void Dispose()
    {
        _handler.Dispose();
        _httpClient.Dispose();
        _provider?.Dispose();
    }

    private static ProviderRequest StubRequest()
    {
        var msg = new Message(MessageRole.User, "hi");
        var tool = new ToolDefinition("tool", "", JsonDocument.Parse("{}"), true, true);
        return new ProviderRequest([msg], [tool]);
    }

    [Fact]
    public async Task UrlIncludesDeploymentAndApiVersion()
    {
        _handler.RespondWith("data: {\"id\":\"x\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"}}]}\ndata: [DONE]\n\n");
        _provider = new AzureOpenAIProvider(_httpClient, "https://myresource.openai.azure.com", "my-deploy", "2024-08-01-preview");

        await _provider.StreamChatAsync(StubRequest(), default).ToListAsync();

        _handler.LastRequestUri!.AbsolutePath.Should().Be("/openai/deployments/my-deploy/chat/completions");
        _handler.LastRequestUri.Query.Should().Contain("api-version=2024-08-01-preview");
    }

    [Fact]
    public async Task BodyUsesDeploymentNameInTheModelField()
    {
        _handler.RespondWith("data: {\"id\":\"x\",\"choices\":[{\"index\":0,\"delta\":{}}]}\ndata: [DONE]\n\n");
        _provider = new AzureOpenAIProvider(_httpClient, "https://r.openai.azure.com", "gpt-4o-prod");

        await _provider.StreamChatAsync(StubRequest(), default).ToListAsync();

        _handler.LastBody.Should().Contain("\"model\":\"gpt-4o-prod\"");
    }

    [Fact]
    public async Task ReusesOpenAICompatStreamingForTextDeltas()
    {
        _handler.RespondWith("""
            data: {"id":"x","choices":[{"index":0,"delta":{"content":"hello"}}]}
            data: {"id":"x","choices":[{"index":0,"delta":{"content":" world"}}]}
            data: [DONE]

            """);
        _provider = new AzureOpenAIProvider(_httpClient, "https://r.openai.azure.com", "d");

        var chunks = await _provider.StreamChatAsync(StubRequest(), default).ToListAsync();

        chunks.Where(c => !string.IsNullOrEmpty(c.TextDelta)).Select(c => c.TextDelta)
            .Should().Equal("hello", " world");
    }

    [Fact]
    public async Task HttpErrorThrowsWithStatus()
    {
        _handler.RespondWith("nope", HttpStatusCode.Unauthorized);
        _provider = new AzureOpenAIProvider(_httpClient, "https://r.openai.azure.com", "d");

        var act = async () => await _provider.StreamChatAsync(StubRequest(), default).ToListAsync();

        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EncodesSpecialCharactersInDeploymentAndApiVersion()
    {
        _handler.RespondWith("data: {\"choices\":[{\"index\":0,\"delta\":{}}]}\ndata: [DONE]\n\n");
        _provider = new AzureOpenAIProvider(_httpClient, "https://r.openai.azure.com", "deploy with space", "2024 preview");

        await _provider.StreamChatAsync(StubRequest(), default).ToListAsync();

        _handler.LastRequestUri!.AbsolutePath.Should().Be("/openai/deployments/deploy%20with%20space/chat/completions");
        _handler.LastRequestUri.Query.Should().Contain("api-version=2024%20preview");
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<(string Body, HttpStatusCode Status)> _responses = new();
        public string? LastBody { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        public void RespondWith(string payload, HttpStatusCode status = HttpStatusCode.OK) =>
            _responses.Enqueue((payload, status));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

            var (payload, status) = _responses.Dequeue();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "text/event-stream"),
                RequestMessage = request
            };
        }
    }
}
