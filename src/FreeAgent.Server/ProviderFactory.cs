using FreeAgent.Kernel;
using FreeAgent.Host;

namespace FreeAgent.Server;

/// <summary>
/// Picks the active <see cref="IProvider"/> from the same env-var/config matrix the host CLI uses.
/// Wrapped as a service so handlers can inject it without recomputing on every request. Provider
/// instances are returned per call (each owns an <see cref="HttpClient"/> with infinite timeout for
/// streaming) — sharing one across the lifetime of the process would require thread-safety claims
/// the adapters don't make.
/// </summary>
public sealed class ProviderFactory
{
    private ProviderConfig _config = ProviderConfig.Load();

    /// <summary>The currently loaded user config — read by the config endpoints to report state.</summary>
    public ProviderConfig Config => _config;

    /// <summary>
    /// Re-reads the user config from disk. Called after a config-write endpoint mutates the file so
    /// that subsequently-created sessions pick up the new provider/key without a server restart.
    /// </summary>
    public void Reload() => _config = ProviderConfig.Load();

    public (IProvider Provider, string Model, string ProviderName) Create()
    {
        var name = _config.ResolveProvider();
        var settings = _config.SettingsFor(name);
        var baseUrl = settings.BaseUrl!;
        // Don't hard-fail session creation when no key is configured (the provider ctors throw on an
        // empty key). The server creates/lists/deletes sessions without needing a live provider; a
        // placeholder lets construction succeed and defers any auth failure to the actual turn — unlike
        // the CLI, which bootstraps the key up front.
        var apiKey = string.IsNullOrWhiteSpace(settings.ApiKey) ? "unset" : settings.ApiKey;
        var model = settings.Model!;

        IProvider provider = name switch
        {
            "anthropic" => new AnthropicProvider(baseUrl, apiKey, model),
            "azure" => new AzureOpenAIProvider(baseUrl, apiKey, model, settings.ApiVersion ?? AzureOpenAIProvider.DefaultApiVersion),
            "ollama" => new OllamaProvider(baseUrl, model),
            "bedrock" => new BedrockProvider(region: baseUrl, modelId: model),
            "vertex" => new VertexProvider(projectId: baseUrl, location: settings.ApiVersion ?? ProviderConfig.VertexDefaultLocation, modelId: model),
            _ => new OpenAIProvider(baseUrl, apiKey, model),
        };
        return (provider, model, name);
    }
}
