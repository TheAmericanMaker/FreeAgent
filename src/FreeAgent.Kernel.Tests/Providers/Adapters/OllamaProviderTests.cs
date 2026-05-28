using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace FreeAgent.Kernel.Tests.Providers.Adapters;

public sealed class OllamaProviderTests : IDisposable
{
    private readonly FakeOllamaHandler _handler = new();
    private readonly HttpClient _httpClient;
    private OllamaProvider _provider = null!;

    public OllamaProviderTests()
    {
        _httpClient = new HttpClient(_handler) { BaseAddress = new Uri("http://fake.local/") };
    }

    public void Dispose()
    {
        _handler.Dispose();
        _httpClient.Dispose();
        _provider?.Dispose();
    }

    private void WireResponse(string ndjson, HttpStatusCode status = HttpStatusCode.OK) =>
        _handler.RespondWith(ndjson, status);

    private static ProviderRequest StubRequest()
    {
        var msg = new Message(MessageRole.User, "Hello");
        var tool = new ToolDefinition("read_file", "Read a file.", JsonDocument.Parse("{\"type\":\"object\"}"), true, true);
        return new ProviderRequest([msg], [tool]);
    }

    private OllamaProvider NewProvider() => new(_httpClient, "http://fake.local/");

    // ── streaming format ──────────────────────────────────────────────────────

    [Fact]
    public async Task ConcatenatesTextDeltasAcrossNdjsonLines()
    {
        WireResponse(
            "{\"model\":\"x\",\"message\":{\"role\":\"assistant\",\"content\":\"hello\"},\"done\":false}\n" +
            "{\"model\":\"x\",\"message\":{\"role\":\"assistant\",\"content\":\" world\"},\"done\":false}\n" +
            "{\"model\":\"x\",\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"prompt_eval_count\":12,\"eval_count\":5}\n");
        _provider = NewProvider();

        var chunks = await _provider.StreamChatAsync(StubRequest(), default).ToListAsync();

        chunks.Where(c => !string.IsNullOrEmpty(c.TextDelta)).Select(c => c.TextDelta)
            .Should().Equal("hello", " world");
        var usage = chunks.Select(c => c.Usage).FirstOrDefault(u => u is not null);
        usage.Should().NotBeNull();
        usage!.InputTokens.Should().Be(12);
        usage.OutputTokens.Should().Be(5);
        chunks.Should().Contain(c => c.IsComplete);
    }

    [Fact]
    public async Task ToolCallsAreEmittedWithSyntheticIdsAndRawArguments()
    {
        WireResponse(
            "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"function\":{\"name\":\"read_file\",\"arguments\":{\"path\":\"a.cs\"}}}]},\"done\":false}\n" +
            "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"prompt_eval_count\":1,\"eval_count\":1}\n");
        _provider = NewProvider();

        var chunks = await _provider.StreamChatAsync(StubRequest(), default).ToListAsync();
        var toolDeltas = chunks.Where(c => c.ToolCallDelta is not null).Select(c => c.ToolCallDelta!).ToList();

        toolDeltas.Should().HaveCount(1);
        toolDeltas[0].Name.Should().Be("read_file");
        toolDeltas[0].ArgumentsJson.Should().Contain("\"path\":\"a.cs\"");
        toolDeltas[0].Id.Should().Be("call_0");
    }

    [Fact]
    public async Task MultipleToolCallsInOneChunkGetSequentialIds()
    {
        WireResponse(
            "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[" +
                "{\"function\":{\"name\":\"a\",\"arguments\":{}}}," +
                "{\"function\":{\"name\":\"b\",\"arguments\":{}}}" +
            "]},\"done\":true}\n");
        _provider = NewProvider();

        var ids = (await _provider.StreamChatAsync(StubRequest(), default).ToListAsync())
            .Where(c => c.ToolCallDelta is not null)
            .Select(c => c.ToolCallDelta!.Id)
            .ToList();

        ids.Should().Equal("call_0", "call_1");
    }

    [Fact]
    public async Task MalformedJsonLinesAreSkipped()
    {
        WireResponse(
            "not json\n" +
            "{\"message\":{\"content\":\"good\"},\"done\":false}\n" +
            "{\"done\":true}\n");
        _provider = NewProvider();

        var chunks = await _provider.StreamChatAsync(StubRequest(), default).ToListAsync();

        chunks.Where(c => c.TextDelta is not null).Select(c => c.TextDelta).Should().Equal("good");
        chunks.Should().Contain(c => c.IsComplete);
    }

    [Theory]
    [InlineData("stop", StopReason.EndTurn)]
    [InlineData("length", StopReason.MaxTokens)]
    [InlineData("load", StopReason.Unknown)]
    public async Task MapsDoneReasonToNormalizedStopReason(string doneReason, StopReason expected)
    {
        WireResponse($"{{\"message\":{{\"content\":\"hi\"}},\"done\":true,\"done_reason\":\"{doneReason}\"}}\n");
        _provider = NewProvider();

        var chunks = await _provider.StreamChatAsync(StubRequest(), default).ToListAsync();

        chunks.Should().Contain(c => c.IsComplete && c.StopReason == expected);
    }

    [Fact]
    public async Task EmitsSyntheticCompleteWhenStreamEndsWithoutDone()
    {
        WireResponse("{\"message\":{\"content\":\"hi\"},\"done\":false}\n");
        _provider = NewProvider();

        var chunks = await _provider.StreamChatAsync(StubRequest(), default).ToListAsync();

        chunks.Should().Contain(c => c.IsComplete);
    }

    // ── request body shape ────────────────────────────────────────────────────

    [Fact]
    public async Task RequestTargetsApiChatPath()
    {
        WireResponse("{\"done\":true}\n");
        _provider = NewProvider();

        await _provider.StreamChatAsync(StubRequest(), default).ToListAsync();

        _handler.LastRequestUri!.AbsolutePath.Should().EndWith("/api/chat");
    }

    [Fact]
    public async Task RequestBodyIncludesModelStreamMessagesAndTools()
    {
        WireResponse("{\"done\":true}\n");
        _provider = new OllamaProvider(_httpClient, "http://fake.local/", "qwen-test");

        await _provider.StreamChatAsync(StubRequest(), default).ToListAsync();

        var body = _handler.LastBody!;
        body.Should().Contain("\"model\":\"qwen-test\"");
        body.Should().Contain("\"stream\":true");
        body.Should().Contain("\"role\":\"user\"").And.Contain("\"content\":\"Hello\"");
        body.Should().Contain("\"tools\":");
        body.Should().Contain("\"name\":\"read_file\"");
    }

    [Fact]
    public async Task OmitsOptionsWhenNoTuningProvided()
    {
        WireResponse("{\"done\":true}\n");
        _provider = NewProvider();

        await _provider.StreamChatAsync(StubRequest(), default).ToListAsync();

        _handler.LastBody.Should().NotContain("\"options\"");
    }

    [Fact]
    public async Task EmitsOptionsWhenNumCtxOrTemperatureSet()
    {
        WireResponse("{\"done\":true}\n");
        _provider = new OllamaProvider(_httpClient, "http://fake.local/", "m", numCtx: 8192, temperature: 0.3);

        await _provider.StreamChatAsync(StubRequest(), default).ToListAsync();

        _handler.LastBody.Should()
            .Contain("\"options\":{").And
            .Contain("\"num_ctx\":8192").And
            .Contain("\"temperature\":0.3");
    }

    [Fact]
    public async Task PriorAssistantToolCallsRoundTripIntoRequestBody()
    {
        WireResponse("{\"done\":true}\n");
        _provider = NewProvider();

        var req = new ProviderRequest([
            new Message(MessageRole.User, "do it"),
            new Message(MessageRole.Assistant, "", [new ToolCall("call_0", "read_file", "{\"path\":\"a.cs\"}")]),
            new Message(MessageRole.Tool, "the contents", ToolCallId: "call_0", ToolName: "read_file"),
        ], []);

        await _provider.StreamChatAsync(req, default).ToListAsync();

        var body = _handler.LastBody!;
        body.Should().Contain("\"role\":\"assistant\"")
            .And.Contain("\"tool_calls\":")
            .And.Contain("\"name\":\"read_file\"")
            .And.Contain("\"path\":\"a.cs\"")
            .And.Contain("\"role\":\"tool\"")
            .And.Contain("\"content\":\"the contents\"");
    }

    [Fact]
    public async Task NonSuccessResponseThrowsWithBody()
    {
        WireResponse("model not found", HttpStatusCode.NotFound);
        _provider = NewProvider();

        var act = async () => await _provider.StreamChatAsync(StubRequest(), default).ToListAsync();

        await act.Should().ThrowAsync<HttpRequestException>()
            .Where(e => e.Message.Contains("404") && e.Message.Contains("model not found"));
    }

    // ── fake handler ──────────────────────────────────────────────────────────

    private sealed class FakeOllamaHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpContent Content, HttpStatusCode Status)> _responses = new();
        public string? LastBody { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        public void RespondWith(string payload, HttpStatusCode status = HttpStatusCode.OK)
        {
            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/x-ndjson");
            _responses.Enqueue((content, status));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                await Task.FromCanceled<HttpResponseMessage>(cancellationToken);

            LastRequestUri = request.RequestUri;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

            var (content, status) = _responses.Dequeue();
            return new HttpResponseMessage(status) { Content = content, RequestMessage = request };
        }
    }
}
