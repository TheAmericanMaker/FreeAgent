using System.Text.Json;
using System.Text.Json.Nodes;

namespace FreeAgent.Host;

/// <summary>
/// Interactive provider-configuration wizard. Run via <c>freeagent setup</c>. The interactive
/// loop lives in <see cref="RunAsync"/>; the pure helpers (<see cref="ParseProviderChoice"/>,
/// <see cref="MergeProviderSection"/>, <see cref="DescribeProvider"/>) are static and unit-tested
/// so the wizard's logic isn't tied to <see cref="Console"/> I/O.
/// </summary>
public static class InteractiveSetup
{
    /// <summary>
    /// Provider identifiers the wizard knows how to configure. Order matters — the menu prints
    /// them in this order and the numeric choice maps 1→<c>openai</c>, 2→<c>anthropic</c>, etc.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedProviders =
        ["openai", "anthropic", "azure", "ollama", "bedrock", "vertex"];

    /// <summary>
    /// Parse the user's response to the provider-pick prompt. Accepts either the menu number
    /// (1–N) or the provider name (case-insensitive). Returns null for blank / unrecognized input
    /// so the caller can re-prompt.
    /// </summary>
    public static string? ParseProviderChoice(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var trimmed = input.Trim();

        if (int.TryParse(trimmed, out var n) && n >= 1 && n <= SupportedProviders.Count)
            return SupportedProviders[n - 1];

        var lower = trimmed.ToLowerInvariant();
        return SupportedProviders.Contains(lower) ? lower : null;
    }

    /// <summary>
    /// One-line description of each provider — what auth it uses, where it lives. Drives the
    /// menu output so the user can pick without leaving the wizard.
    /// </summary>
    public static string DescribeProvider(string provider) => provider switch
    {
        "openai"    => "OpenAI / any OpenAI-compatible /v1/chat/completions endpoint (Groq, gateways, …)",
        "anthropic" => "Anthropic Claude (x-api-key header)",
        "azure"     => "Azure OpenAI (endpoint + deployment + api-version)",
        "ollama"    => "Ollama — local or Ollama Cloud (api key optional, only needed for direct cloud access)",
        "bedrock"   => "AWS Bedrock — auth from the default AWS credential chain",
        "vertex"    => "Google Vertex AI — auth from Application Default Credentials (gcloud auth)",
        _           => provider,
    };

    /// <summary>
    /// The questions to ask for a given provider, in order. Each question carries a property name
    /// (which slot in the JSON config to populate), a prompt label, an optional default, and a
    /// hint about whether the answer is a secret (so the I/O layer can mask the input).
    /// </summary>
    public static IReadOnlyList<SetupQuestion> QuestionsFor(string provider) => provider switch
    {
        "openai" =>
        [
            new("apiKey",  "API key",          Default: null, Secret: true,  EnvFallback: "OPENAI_API_KEY"),
            new("baseUrl", "Base URL",         Default: "https://api.openai.com/v1", Secret: false),
            new("model",   "Model",            Default: "gpt-4o-mini",               Secret: false),
        ],
        "anthropic" =>
        [
            new("apiKey",  "API key",  Default: null, Secret: true,  EnvFallback: "ANTHROPIC_API_KEY"),
            new("baseUrl", "Base URL", Default: "https://api.anthropic.com",  Secret: false),
            new("model",   "Model",    Default: "claude-3-7-sonnet-latest",   Secret: false),
        ],
        "azure" =>
        [
            new("apiKey",     "API key",        Default: null, Secret: true,  EnvFallback: "AZURE_OPENAI_API_KEY"),
            new("baseUrl",    "Endpoint",       Default: null, Secret: false, EnvFallback: "AZURE_OPENAI_ENDPOINT"),
            new("model",      "Deployment",     Default: null, Secret: false, EnvFallback: "AZURE_OPENAI_DEPLOYMENT"),
            new("apiVersion", "API version",    Default: "2024-08-01-preview", Secret: false),
        ],
        "ollama" =>
        [
            new("baseUrl", "Host",    Default: "http://localhost:11434", Secret: false, EnvFallback: "OLLAMA_HOST"),
            new("apiKey",  "API key", Default: null, Secret: true, Hint: "Only needed for direct Ollama Cloud access (https://ollama.com). Leave blank for local Ollama.", EnvFallback: "OLLAMA_API_KEY"),
            new("model",   "Model",   Default: "qwen2.5-coder",          Secret: false),
        ],
        "bedrock" =>
        [
            // BaseUrl carries the AWS region for the Bedrock provider; the SDK builds the actual
            // endpoint from RegionEndpoint.GetBySystemName(...).
            new("baseUrl", "AWS region",   Default: "us-east-1", Secret: false, EnvFallback: "AWS_REGION"),
            new("model",   "Bedrock model id", Default: "anthropic.claude-3-7-sonnet-20250219-v1:0", Secret: false),
        ],
        "vertex" =>
        [
            // BaseUrl carries the GCP project id; ApiVersion carries the location.
            new("baseUrl",    "GCP project id", Default: null, Secret: false, EnvFallback: "VERTEX_PROJECT"),
            new("apiVersion", "GCP location",   Default: "us-central1", Secret: false, EnvFallback: "VERTEX_LOCATION"),
            new("model",      "Vertex model id", Default: "claude-3-7-sonnet@20250219", Secret: false),
        ],
        _ => [],
    };

    /// <summary>
    /// Merge a freshly-collected provider section into the existing config JSON. The wizard updates
    /// only the requested provider — other sections (including any legacy flat fields) are
    /// preserved unchanged so a user with both <c>openai</c> and <c>anthropic</c> configured
    /// doesn't lose one when re-running setup for the other. Existing keys in the same provider
    /// section are also kept unless the new answers replace them. Pure — no I/O.
    /// </summary>
    public static string MergeProviderSection(string? existingJson, string provider, IReadOnlyDictionary<string, string> answers, bool setAsDefault)
    {
        JsonObject root;
        if (string.IsNullOrWhiteSpace(existingJson))
        {
            root = new JsonObject();
        }
        else
        {
            try { root = JsonNode.Parse(existingJson)?.AsObject() ?? new JsonObject(); }
            catch (JsonException) { root = new JsonObject(); }
        }

        if (root[provider] is not JsonObject section)
        {
            section = new JsonObject();
            root[provider] = section;
        }
        foreach (var (key, value) in answers)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            section[key] = value;
        }

        if (setAsDefault)
            root["provider"] = provider;

        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Read a provider-section value from an existing config document. Pure helper so setup can
    /// preserve values when it is re-run.
    /// </summary>
    public static string? ExistingProviderValue(string? existingJson, string provider, string slot)
    {
        if (string.IsNullOrWhiteSpace(existingJson)) return null;
        try
        {
            var root = JsonNode.Parse(existingJson)?.AsObject();
            var providerValue = root?[provider]?[slot]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(providerValue))
                return providerValue;

            return string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase)
                ? root?[slot]?.GetValue<string>()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolve a question's pre-filled default: env-var fallback → existing config → explicit ctor
    /// default → null. Pure helper so the I/O layer can render the right hint inline.
    /// </summary>
    public static string? ResolveDefault(SetupQuestion q, string? existingValue = null)
    {
        if (!string.IsNullOrWhiteSpace(q.EnvFallback)
            && Environment.GetEnvironmentVariable(q.EnvFallback) is { Length: > 0 } env)
        {
            return env;
        }
        if (!string.IsNullOrWhiteSpace(existingValue))
            return existingValue;
        return q.Default;
    }

    /// <summary>
    /// Drive the wizard against the real <see cref="Console"/>. Returns the path the config was
    /// written to (so the caller can print it), or null if the user aborted.
    /// </summary>
    public static async Task<string?> RunAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine();
        Console.WriteLine("FreeAgent — interactive setup");
        Console.WriteLine($"Config file: {ProviderConfig.ConfigPath()}");
        Console.WriteLine();
        Console.WriteLine("Pick a provider:");
        for (var i = 0; i < SupportedProviders.Count; i++)
            Console.WriteLine($"  {i + 1}) {SupportedProviders[i],-9}  {DescribeProvider(SupportedProviders[i])}");

        string? provider = null;
        while (provider is null)
        {
            Console.Write("\nChoice [1]: ");
            var raw = Console.ReadLine();
            if (raw is null) return null; // EOF — user aborted
            if (string.IsNullOrWhiteSpace(raw))
            {
                provider = SupportedProviders[0];
                break;
            }
            provider = ParseProviderChoice(raw);
            if (provider is null)
                Console.WriteLine($"  '{raw.Trim()}' isn't one of the choices — type a number 1–{SupportedProviders.Count} or a provider name.");
        }

        Console.WriteLine();
        Console.WriteLine($"Configuring '{provider}' — {DescribeProvider(provider)}");
        Console.WriteLine("Press Enter to accept the default in [brackets]. Leave blank to skip a value.");
        Console.WriteLine();

        var configPath = ProviderConfig.ConfigPath();
        var existing = File.Exists(configPath) ? await File.ReadAllTextAsync(configPath, cancellationToken) : null;
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var question in QuestionsFor(provider))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var def = ResolveDefault(question, ExistingProviderValue(existing, provider, question.Slot));
            if (!string.IsNullOrWhiteSpace(question.Hint))
                Console.WriteLine($"  {question.Hint}");
            var label = question.Secret
                ? $"  {question.PromptLabel} (input hidden{(def is null ? "" : "; press Enter to keep existing")}): "
                : $"  {question.PromptLabel}{(def is null ? "" : $" [{def}]")}: ";
            Console.Write(label);
            var input = question.Secret ? ReadSecret() : Console.ReadLine();
            if (input is null) return null;
            var value = string.IsNullOrWhiteSpace(input) ? def : input.Trim();
            if (!string.IsNullOrEmpty(value)) answers[question.Slot] = value;
        }

        var setAsDefault = AskYesNo($"Set '{provider}' as the default provider for new shells", defaultYes: true);

        var merged = MergeProviderSection(existing, provider, answers, setAsDefault);

        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, merged, cancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            // chmod 600 — config holds API keys, keep it out of group/other reads.
            try { File.SetUnixFileMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
        }

        Console.WriteLine();
        Console.WriteLine($"✓ Wrote {configPath} (chmod 600).");
        Console.WriteLine();
        Console.WriteLine("Run 'freeagent' from any project directory to start a session.");
        if (provider is not "ollama" and not "bedrock" and not "vertex"
            && !answers.ContainsKey("apiKey"))
        {
            Console.WriteLine($"Note: you skipped the API key — set the env var or re-run 'freeagent setup' before starting a session.");
        }
        return configPath;
    }

    /// <summary>
    /// Read a single line of input without echoing the characters. Returns the raw string (no
    /// trailing newline). Used for API keys so the secret doesn't end up in scrollback. Falls
    /// back to <see cref="Console.ReadLine"/> when stdin isn't a TTY (piped input).
    /// </summary>
    private static string? ReadSecret()
    {
        if (Console.IsInputRedirected) return Console.ReadLine();
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return sb.ToString(); }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }
            if (key.KeyChar >= ' ') sb.Append(key.KeyChar);
        }
    }

    private static bool AskYesNo(string prompt, bool defaultYes)
    {
        var hint = defaultYes ? "[Y/n]" : "[y/N]";
        while (true)
        {
            Console.Write($"  {prompt}? {hint} ");
            var input = Console.ReadLine();
            if (input is null) return defaultYes; // EOF
            var trimmed = input.Trim().ToLowerInvariant();
            if (trimmed.Length == 0) return defaultYes;
            if (trimmed is "y" or "yes") return true;
            if (trimmed is "n" or "no") return false;
            Console.WriteLine("  Please answer y or n.");
        }
    }
}

/// <summary>A single prompt in the wizard. <see cref="Slot"/> is the JSON key under the provider section.</summary>
public sealed record SetupQuestion(
    string Slot,
    string PromptLabel,
    string? Default = null,
    bool Secret = false,
    string? EnvFallback = null,
    string? Hint = null);
