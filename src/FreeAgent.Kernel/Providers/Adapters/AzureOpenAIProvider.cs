using System.Runtime.CompilerServices;
using System.Text;

namespace FreeAgent.Kernel;

/// <summary>
/// Azure OpenAI Service provider. Same wire format as <see cref="OpenAIProvider"/> (shared via
/// <see cref="OpenAICompatStreaming"/>), with Azure-specific URL construction —
/// <c>{endpoint}/openai/deployments/{deployment}/chat/completions?api-version=...</c> — and the
/// <c>api-key</c> header in place of Bearer.
/// </summary>
public sealed class AzureOpenAIProvider : IProvider, IDisposable
{
    public const string DefaultApiVersion = "2024-08-01-preview";

    private readonly string _deployment;
    private readonly string _apiVersion;
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly bool _ownsClient;
    private bool _disposed;

    /// <summary>
    /// <paramref name="endpoint"/> is the resource endpoint, e.g. <c>https://myresource.openai.azure.com</c>.
    /// <paramref name="deployment"/> is the deployment name configured in Azure (often distinct from the model id).
    /// </summary>
    public AzureOpenAIProvider(string endpoint, string apiKey, string deployment, string apiVersion = DefaultApiVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(deployment);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiVersion);

        _deployment = deployment;
        _apiVersion = apiVersion;
        _ownsClient = true;
        _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _httpClient.DefaultRequestHeaders.Add("api-key", apiKey);
        _baseUri = new Uri(endpoint.TrimEnd('/') + "/");
    }

    public AzureOpenAIProvider(HttpClient httpClient, string endpoint, string deployment, string apiVersion = DefaultApiVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(deployment);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiVersion);

        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _deployment = deployment;
        _apiVersion = apiVersion;
        _ownsClient = false;
        _baseUri = new Uri(endpoint.TrimEnd('/') + "/");
    }

    public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
        ProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var relative = $"openai/deployments/{Uri.EscapeDataString(_deployment)}/chat/completions?api-version={Uri.EscapeDataString(_apiVersion)}";
        var endpoint = new Uri(_baseUri, relative);

        // Azure's model field in the body is the deployment name (NOT the model id).
        var body = OpenAICompatStreaming.BuildRequestBody(_deployment, request);
        using var httpContent = new StringContent(body, Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = httpContent };
        var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Azure OpenAI returned {(int)response.StatusCode} ({response.StatusCode}): {error}",
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
        if (_disposed) return;
        if (_ownsClient) _httpClient.Dispose();
        _disposed = true;
    }
}
