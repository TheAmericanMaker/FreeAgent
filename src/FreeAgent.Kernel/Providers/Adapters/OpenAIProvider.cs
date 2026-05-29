using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;

namespace FreeAgent.Kernel;

/// <summary>
/// OpenAI-compatible streaming provider adapter (<c>POST {baseUrl}/chat/completions</c>, Bearer
/// auth). Most of the wire-format work lives in <see cref="OpenAICompatStreaming"/> so this class
/// owns only URL construction, auth, and HTTP plumbing.
/// </summary>
public sealed class OpenAIProvider : IProvider, IDisposable
{
    private readonly string _model;
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly bool _ownsClient;
    private bool _disposed;

    public OpenAIProvider(string baseUrl, string apiKey, string model = "gpt-4o")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        _model = model;
        _ownsClient = true;
        // No request timeout: a streamed completion can legitimately run for minutes, and the
        // default 100s HttpClient.Timeout would abort it. Per-call cancellation comes from the
        // CancellationToken passed to StreamChatAsync instead.
        _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
    }

    public OpenAIProvider(HttpClient httpClient, string baseUrl, string model = "gpt-4o")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _model = model;
        _ownsClient = false;
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
    }

    public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
        ProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var body = OpenAICompatStreaming.BuildRequestBody(_model, request);
        using var httpContent = new StringContent(body, Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "chat/completions"))
        {
            Content = httpContent
        };
        // ResponseHeadersRead returns as soon as the headers arrive, so the SSE body is read
        // incrementally below. The default (ResponseContentRead) would buffer the entire
        // response before returning, collapsing the stream into a single burst.
        using var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"OpenAI API returned {(int)response.StatusCode} ({response.StatusCode}): {error}",
                inner: null,
                statusCode: response.StatusCode);
        }

        await foreach (var chunk in OpenAICompatStreaming.ReadStreamAsync(response, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            yield return chunk;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_ownsClient)
            _httpClient.Dispose();

        _disposed = true;
    }
}
