using System.Net;
using System.Net.Http.Headers;
using FreeAgent.Host;

namespace FreeAgent.Server;

/// <summary>
/// Best-effort credential / reachability check for the setup UI's "Test connection" button. Uses the
/// cheapest read-only call each provider exposes (a model-list or tag-list endpoint) so verifying a
/// key costs nothing and never sends a chat completion. Fields not supplied in the request fall back
/// to the saved config, so the UI can re-test a provider whose (masked) key it didn't re-send.
/// Providers whose auth lives in an ambient cloud credential chain (Bedrock, Vertex) can't be probed
/// cheaply and report a "skipped — verified on first turn" result rather than a false negative.
/// </summary>
public static class ProviderProbe
{
    // Short timeout: this backs an interactive button, so failing fast beats hanging the UI.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public static async Task<ProbeResult> TestAsync(TestProviderRequest req, CancellationToken ct)
    {
        var provider = req.Provider.Trim().ToLowerInvariant();
        var saved = ProviderConfig.Load().SettingsFor(provider);
        var baseUrl = Coalesce(req.BaseUrl, saved.BaseUrl);
        var apiKey = Coalesce(req.ApiKey, saved.ApiKey);
        var apiVersion = Coalesce(req.ApiVersion, saved.ApiVersion);

        try
        {
            return provider switch
            {
                "openai" => await ProbeOpenAiAsync(baseUrl, apiKey, ct),
                "anthropic" => await ProbeAnthropicAsync(baseUrl, apiKey, ct),
                "ollama" => await ProbeOllamaAsync(baseUrl, ct),
                "azure" => await ProbeAzureAsync(baseUrl, apiKey, apiVersion, ct),
                "bedrock" => new ProbeResult(true, "Bedrock uses the AWS credential chain — verified on the first turn.", "skipped"),
                "vertex" => new ProbeResult(true, "Vertex uses Application Default Credentials — verified on the first turn.", "skipped"),
                _ => new ProbeResult(false, $"Unknown provider '{provider}'.", "fields"),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProbeResult(false, "Timed out connecting to the provider.", "live");
        }
        catch (HttpRequestException ex)
        {
            return new ProbeResult(false, $"Couldn't reach the provider: {ex.Message}", "live");
        }
    }

    private static async Task<ProbeResult> ProbeOpenAiAsync(string? baseUrl, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return new ProbeResult(false, "No API key set.", "fields");
        var url = $"{(baseUrl ?? ProviderConfig.DefaultBaseUrl).TrimEnd('/')}/models";
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var resp = await Http.SendAsync(msg, ct);
        return FromStatus(resp, "OpenAI");
    }

    private static async Task<ProbeResult> ProbeAnthropicAsync(string? baseUrl, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return new ProbeResult(false, "No API key set.", "fields");
        var url = $"{(baseUrl ?? ProviderConfig.AnthropicDefaultBaseUrl).TrimEnd('/')}/v1/models";
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        msg.Headers.Add("x-api-key", apiKey);
        msg.Headers.Add("anthropic-version", "2023-06-01");
        using var resp = await Http.SendAsync(msg, ct);
        return FromStatus(resp, "Anthropic");
    }

    private static async Task<ProbeResult> ProbeOllamaAsync(string? baseUrl, CancellationToken ct)
    {
        var url = $"{(baseUrl ?? ProviderConfig.OllamaDefaultBaseUrl).TrimEnd('/')}/api/tags";
        using var resp = await Http.GetAsync(url, ct);
        return resp.IsSuccessStatusCode
            ? new ProbeResult(true, "Ollama is reachable.", "live")
            : new ProbeResult(false, $"Ollama returned {(int)resp.StatusCode} {resp.ReasonPhrase}.", "live");
    }

    private static async Task<ProbeResult> ProbeAzureAsync(string? endpoint, string? apiKey, string? apiVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return new ProbeResult(false, "No Azure endpoint set.", "fields");
        if (string.IsNullOrWhiteSpace(apiKey)) return new ProbeResult(false, "No API key set.", "fields");
        var ver = string.IsNullOrWhiteSpace(apiVersion) ? "2024-08-01-preview" : apiVersion;
        var url = $"{endpoint.TrimEnd('/')}/openai/models?api-version={ver}";
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        msg.Headers.Add("api-key", apiKey);
        using var resp = await Http.SendAsync(msg, ct);
        return FromStatus(resp, "Azure OpenAI");
    }

    private static ProbeResult FromStatus(HttpResponseMessage resp, string label) =>
        resp.IsSuccessStatusCode
            ? new ProbeResult(true, $"{label} reachable — the key works.", "live")
            : resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? new ProbeResult(false, $"{label} rejected the API key ({(int)resp.StatusCode}).", "live")
                : new ProbeResult(false, $"{label} returned {(int)resp.StatusCode} {resp.ReasonPhrase}.", "live");

    private static string? Coalesce(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) ? a : b;
}

/// <summary><c>Mode</c> is <c>live</c> (an actual call was made), <c>fields</c> (failed local validation),
/// or <c>skipped</c> (provider uses a cloud credential chain that can't be probed cheaply).</summary>
public sealed record ProbeResult(bool Ok, string Message, string Mode);
