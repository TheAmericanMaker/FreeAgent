using System.Text.Json;
using FreeAgent.Kernel;

namespace FreeAgent.Host;

/// <summary>Per-provider connection settings. <see cref="ApiVersion"/> is currently Azure-only.</summary>
public sealed record ProviderSettings(string? BaseUrl, string? ApiKey, string? Model, string? ApiVersion = null);

/// <summary>
/// User-level config so the bare <c>freeagent</c> command works without exporting env vars in every
/// shell. Resolution precedence is <b>provider-specific env var &gt; provider section in the config
/// &gt; legacy flat field &gt; built-in default</b>. The file is XDG-aware
/// (<c>$XDG_CONFIG_HOME/freeagent/config.json</c>, else <c>~/.config/freeagent/config.json</c>) and is
/// distinct from the per-project <c>.freeagent/config.json</c>, which holds permission rules.
/// <para>
/// Schema (all fields optional):
/// <code>
/// {
///   "provider": "openai" | "anthropic",    // selects the IProvider
///   "baseUrl": "...", "apiKey": "...", "model": "...",   // legacy flat = openai defaults
///   "openai":    { "baseUrl": "...", "apiKey": "...", "model": "..." },
///   "anthropic": { "baseUrl": "...", "apiKey": "...", "model": "..." }
/// }
/// </code>
/// </para>
/// </summary>
public sealed class ProviderConfig
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";
    public const string DefaultModel = "gpt-4o-mini";
    public const string AnthropicDefaultBaseUrl = "https://api.anthropic.com";
    public const string AnthropicDefaultModel = "claude-3-7-sonnet-latest";
    public const string OllamaDefaultBaseUrl = "http://localhost:11434";
    public const string OllamaDefaultModel = "qwen2.5-coder";
    public const string BedrockDefaultRegion = "us-east-1";
    public const string BedrockDefaultModel = BedrockProvider.DefaultModelId;
    public const string VertexDefaultLocation = "us-central1";
    public const string VertexDefaultModel = VertexProvider.DefaultModelId;

    /// <summary>Provider key — <c>openai</c> (default) or <c>anthropic</c>. Env <c>FREEPROVIDER</c> overrides.</summary>
    public string? Provider { get; init; }

    /// <summary>Legacy flat fields — interpreted as OpenAI defaults for back-compat.</summary>
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string? Model { get; init; }

    /// <summary>Optional explicit OpenAI section (takes precedence over the flat fields).</summary>
    public ProviderSettings? Openai { get; init; }

    /// <summary>Optional explicit Anthropic section.</summary>
    public ProviderSettings? Anthropic { get; init; }

    /// <summary>Optional explicit Azure OpenAI section. <c>Model</c> here is the deployment name.</summary>
    public ProviderSettings? Azure { get; init; }

    /// <summary>Optional explicit Ollama section. <c>ApiKey</c> is ignored (Ollama is unauthenticated by default).</summary>
    public ProviderSettings? Ollama { get; init; }

    /// <summary>Optional explicit AWS Bedrock section. <c>BaseUrl</c> is the AWS region (e.g. "us-east-1"); <c>ApiKey</c> is ignored (auth comes from the default AWS credential chain).</summary>
    public ProviderSettings? Bedrock { get; init; }

    /// <summary>Optional explicit Vertex section. <c>BaseUrl</c> is the GCP project id; <c>ApiKey</c> is ignored (auth flows through Application Default Credentials).</summary>
    public ProviderSettings? Vertex { get; init; }

    /// <summary>Vertex-only: GCP location (e.g. "us-central1"). Env <c>VERTEX_LOCATION</c> overrides.</summary>
    public string? VertexLocation { get; init; }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Active provider, normalized to lowercase. Env <c>FREEPROVIDER</c> overrides the config.</summary>
    public string ResolveProvider() =>
        Resolve(Environment.GetEnvironmentVariable("FREEPROVIDER"), Provider, "openai").Trim().ToLowerInvariant();

    /// <summary>
    /// Resolved settings for <paramref name="provider"/>: env (provider-specific) > config section >
    /// legacy flat field (openai only) > built-in default.
    /// </summary>
    public ProviderSettings SettingsFor(string provider)
    {
        if (string.Equals(provider, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderSettings(
                BaseUrl: Resolve(Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL"), Anthropic?.BaseUrl, AnthropicDefaultBaseUrl),
                ApiKey:  Resolve(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),  Anthropic?.ApiKey,  string.Empty),
                Model:   Resolve(
                    Environment.GetEnvironmentVariable("FREEMODEL") ?? Environment.GetEnvironmentVariable("ANTHROPIC_MODEL"),
                    Anthropic?.Model, AnthropicDefaultModel));
        }

        if (string.Equals(provider, "vertex", StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderSettings(
                // For Vertex the "BaseUrl" slot carries the GCP project id; the location is a
                // separate field. Auth lives in Application Default Credentials.
                BaseUrl: Resolve(Environment.GetEnvironmentVariable("VERTEX_PROJECT"), Vertex?.BaseUrl, string.Empty),
                ApiKey:  string.Empty,
                Model:   Resolve(
                    Environment.GetEnvironmentVariable("FREEMODEL") ?? Environment.GetEnvironmentVariable("VERTEX_MODEL"),
                    Vertex?.Model, VertexDefaultModel),
                ApiVersion: Resolve(Environment.GetEnvironmentVariable("VERTEX_LOCATION"), VertexLocation, VertexDefaultLocation));
        }

        if (string.Equals(provider, "bedrock", StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderSettings(
                // For Bedrock the "BaseUrl" slot carries the AWS region — the SDK builds the actual
                // endpoint from RegionEndpoint.GetBySystemName(...).
                BaseUrl: Resolve(Environment.GetEnvironmentVariable("AWS_REGION"), Bedrock?.BaseUrl, BedrockDefaultRegion),
                // No api key: auth lives in the AWS credential chain (env / shared profile / IMDS / SSO).
                ApiKey:  string.Empty,
                Model:   Resolve(
                    Environment.GetEnvironmentVariable("FREEMODEL") ?? Environment.GetEnvironmentVariable("BEDROCK_MODEL"),
                    Bedrock?.Model, BedrockDefaultModel));
        }

        if (string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderSettings(
                BaseUrl: Resolve(Environment.GetEnvironmentVariable("OLLAMA_HOST"), Ollama?.BaseUrl, OllamaDefaultBaseUrl),
                // Ollama doesn't require auth; we still accept and forward an api key field for parity
                // with the other provider sections so a single config schema covers everyone.
                ApiKey:  Resolve(Environment.GetEnvironmentVariable("OLLAMA_API_KEY"),  Ollama?.ApiKey,  string.Empty),
                Model:   Resolve(
                    Environment.GetEnvironmentVariable("FREEMODEL") ?? Environment.GetEnvironmentVariable("OLLAMA_MODEL"),
                    Ollama?.Model, OllamaDefaultModel));
        }

        if (string.Equals(provider, "azure", StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderSettings(
                BaseUrl: Resolve(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT"), Azure?.BaseUrl, string.Empty),
                ApiKey:  Resolve(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"),  Azure?.ApiKey,  string.Empty),
                // For Azure the "model" field is the deployment name.
                Model: Resolve(
                    Environment.GetEnvironmentVariable("FREEMODEL") ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT"),
                    Azure?.Model, string.Empty),
                ApiVersion: Resolve(
                    Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION"), Azure?.ApiVersion,
                    "2024-08-01-preview"));
        }

        return new ProviderSettings(
            BaseUrl: Resolve(Environment.GetEnvironmentVariable("OPENAI_BASE_URL"), Openai?.BaseUrl ?? BaseUrl, DefaultBaseUrl),
            ApiKey:  Resolve(Environment.GetEnvironmentVariable("OPENAI_API_KEY"),  Openai?.ApiKey  ?? ApiKey,  string.Empty),
            Model:   Resolve(Environment.GetEnvironmentVariable("FREEMODEL"),       Openai?.Model   ?? Model,   DefaultModel));
    }

    /// <summary>Pure precedence rule: first non-blank of env, then file, then fallback.</summary>
    public static string Resolve(string? env, string? file, string fallback) =>
        !string.IsNullOrWhiteSpace(env) ? env
        : !string.IsNullOrWhiteSpace(file) ? file
        : fallback;

    public static string ConfigPath()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(configHome, "freeagent", "config.json");
    }

    /// <summary>
    /// Persists a config document to <paramref name="path"/>, creating the directory and (on Unix)
    /// tightening the file to <c>chmod 600</c> since it holds API keys. Shared by the interactive
    /// CLI wizard and the server's config endpoints so both write the file the same way.
    /// </summary>
    public static void WriteFile(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        if (!OperatingSystem.IsWindows())
        {
            // chmod 600 — config holds API keys, keep it out of group/other reads.
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
        }
    }

    /// <summary>Loads the config from <paramref name="path"/> (default <see cref="ConfigPath"/>). A
    /// missing file yields an empty config; a malformed one is a non-fatal warning.</summary>
    public static ProviderConfig Load(string? path = null)
    {
        path ??= ConfigPath();
        if (!File.Exists(path))
            return new ProviderConfig();

        try
        {
            return JsonSerializer.Deserialize<ProviderConfig>(File.ReadAllText(path), JsonOpts) ?? new ProviderConfig();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning: ignoring provider config '{path}': {ex.Message}");
            return new ProviderConfig();
        }
    }
}
