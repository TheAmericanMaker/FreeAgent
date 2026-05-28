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
    private readonly ProviderConfig _config = ProviderConfig.Load();

    public (IProvider Provider, string Model, string ProviderName) Create()
    {
        var name = _config.ResolveProvider();
        var settings = _config.SettingsFor(name);
        var baseUrl = settings.BaseUrl!;
        var apiKey = settings.ApiKey!;
        var model = settings.Model!;

        IProvider provider = name switch
        {
            "anthropic" => new AnthropicProvider(baseUrl, apiKey, model),
            "azure" => new AzureOpenAIProvider(baseUrl, apiKey, model, settings.ApiVersion ?? AzureOpenAIProvider.DefaultApiVersion),
            "ollama" => new OllamaProvider(baseUrl, model),
            "bedrock" => new BedrockProvider(region: baseUrl, modelId: model),
            _ => new OpenAIProvider(baseUrl, apiKey, model),
        };
        return (provider, model, name);
    }
}
