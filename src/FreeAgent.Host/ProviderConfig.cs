using System.Text.Json;

namespace FreeAgent.Host;

/// <summary>
/// Provider/model settings loaded from a user-level config file so the bare <c>freeagent</c> command
/// works without exporting environment variables in every shell. Resolution precedence is
/// <b>environment variable &gt; config file &gt; built-in default</b>. The file is XDG-aware
/// (<c>$XDG_CONFIG_HOME/freeagent/config.json</c>, else <c>~/.config/freeagent/config.json</c>) and is
/// distinct from the per-project <c>.freeagent/config.json</c>, which holds permission rules.
/// </summary>
public sealed record ProviderConfig
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";
    public const string DefaultModel = "gpt-4o-mini";

    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string? Model { get; init; }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public string ResolveBaseUrl() => Resolve(Environment.GetEnvironmentVariable("OPENAI_BASE_URL"), BaseUrl, DefaultBaseUrl);
    public string ResolveApiKey() => Resolve(Environment.GetEnvironmentVariable("OPENAI_API_KEY"), ApiKey, string.Empty);
    public string ResolveModel() => Resolve(Environment.GetEnvironmentVariable("FREEMODEL"), Model, DefaultModel);

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
