using System.Text.Json;
using System.Text.Json.Nodes;
using FreeAgent.Kernel;
using FreeAgent.Host;

namespace FreeAgent.Server;

/// <summary>
/// Configuration + metadata endpoints that let a frontend (the opentui TUI per ADR 0005) drive the
/// whole setup experience over the wire — no <c>freeagent setup</c> terminal wizard, no hand-editing
/// JSON. Mirrors the data the CLI wizard already knows (<see cref="InteractiveSetup"/>) plus the
/// project-level permission/trust surface, so a brand-new user can pick a provider, paste a key,
/// choose a model, set the working directory's permissions, and trust the project entirely from the UI.
///
/// <list type="bullet">
///   <item><c>GET  /providers</c> — supported providers + their setup field schema.</item>
///   <item><c>GET  /models?provider=</c> — known models (capabilities) for a provider's wire API.</item>
///   <item><c>GET  /config</c> — active provider + per-provider settings (API keys redacted).</item>
///   <item><c>PUT  /config/provider</c> — save a provider's settings; optionally make it the default.</item>
///   <item><c>POST /config/provider/test</c> — best-effort live credential/reachability check.</item>
///   <item><c>GET/PUT /config/permissions?dir=</c> — read/write a project's tool/capability rules.</item>
///   <item><c>GET  /config/trust?dir=</c> / <c>POST /config/trust</c> — project workspace-trust state.</item>
/// </list>
/// API keys are never echoed back: <c>GET /config</c> reports only whether a key is set and a short
/// hint (last 4 chars). Writes preserve unspecified fields (a blank API key keeps the existing one).
/// </summary>
public static class ConfigEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static void Map(WebApplication app)
    {
        app.MapGet("/providers", () => Results.Ok(
            InteractiveSetup.SupportedProviders.Select(p => new ProviderInfo(
                Id: p,
                Description: InteractiveSetup.DescribeProvider(p),
                // Ollama is unauthenticated; Bedrock/Vertex use ambient cloud credential chains.
                RequiresApiKey: p is not ("ollama" or "bedrock" or "vertex"),
                Fields: [.. InteractiveSetup.QuestionsFor(p).Select(q => new ProviderField(
                    Slot: q.Slot,
                    Label: q.PromptLabel,
                    Default: InteractiveSetup.ResolveDefault(q),
                    Secret: q.Secret))]))));

        app.MapGet("/models", (string? provider) =>
        {
            var wire = WireApiFor(provider);
            var models = ModelCatalog.Defaults().All
                .Where(m => wire is null || string.Equals(m.WireApi, wire, StringComparison.OrdinalIgnoreCase))
                .Select(m => new ModelInfo(m.Id, m.WireApi, m.ContextTokens, m.DefaultMaxOutputTokens,
                    m.SupportsTools, m.SupportsVision, m.SupportsThinking));
            return Results.Ok(models);
        });

        // Fetch live models from an Ollama host (local or Cloud). Used by the TUI setup wizard
        // so users can pick from a list instead of typing a model name blind.
        app.MapGet("/models/live", async (string? provider, string? baseUrl, string? apiKey, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(baseUrl))
                return Results.Ok(Array.Empty<string>());

            if (!string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase))
                return Results.Ok(Array.Empty<string>());

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/api/tags");
                if (!string.IsNullOrWhiteSpace(apiKey))
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                using var resp = await http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                    return Results.Ok(Array.Empty<string>());
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var models = new List<string>();
                if (doc.RootElement.TryGetProperty("models", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var m in arr.EnumerateArray())
                    {
                        if (m.TryGetProperty("name", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            var s = name.GetString();
                            if (!string.IsNullOrWhiteSpace(s))
                                models.Add(s);
                        }
                    }
                }
                return Results.Ok(models);
            }
            catch
            {
                return Results.Ok(Array.Empty<string>());
            }
        });

        app.MapGet("/config", (ProviderFactory factory) =>
        {
            var cfg = factory.Config;
            var providers = new Dictionary<string, ProviderView>(StringComparer.Ordinal);
            foreach (var p in InteractiveSetup.SupportedProviders)
            {
                var s = cfg.SettingsFor(p);
                providers[p] = new ProviderView(
                    BaseUrl: s.BaseUrl,
                    Model: s.Model,
                    ApiVersion: s.ApiVersion,
                    ApiKeySet: !string.IsNullOrWhiteSpace(s.ApiKey),
                    ApiKeyHint: HintFor(s.ApiKey));
            }
            return Results.Ok(new ConfigView(cfg.ResolveProvider(), ProviderConfig.ConfigPath(), providers));
        });

        app.MapPut("/config/provider", async (UpdateProviderRequest? body, ProviderFactory factory) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Provider))
                return Results.BadRequest(new { message = "Field 'provider' is required." });

            var provider = body.Provider.Trim().ToLowerInvariant();
            if (!InteractiveSetup.SupportedProviders.Contains(provider))
                return Results.BadRequest(new { message = $"Unknown provider '{provider}'." });

            var path = ProviderConfig.ConfigPath();
            var existingJson = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;

            // Start from whatever's already saved for this provider so unspecified fields (notably a
            // blank API key the UI didn't re-send because it was masked) are preserved, then overlay
            // the non-blank fields from the request.
            var answers = ExistingSection(existingJson, provider);
            ApplyField(answers, "apiKey", body.ApiKey);
            ApplyField(answers, "baseUrl", body.BaseUrl);
            ApplyField(answers, "model", body.Model);
            ApplyField(answers, "apiVersion", body.ApiVersion);

            var merged = InteractiveSetup.MergeProviderSection(existingJson, provider, answers, body.SetAsDefault ?? true);
            ProviderConfig.WriteFile(path, merged);
            // Refresh the cached config so sessions created after this write use the new provider/key
            // without a server restart.
            factory.Reload();
            return Results.Ok(new { provider, configPath = path, isDefault = body.SetAsDefault ?? true });
        });

        app.MapPost("/config/provider/test", async (TestProviderRequest? body, CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Provider))
                return Results.BadRequest(new { message = "Field 'provider' is required." });
            var result = await ProviderProbe.TestAsync(body, ct);
            return Results.Ok(result);
        });

        app.MapGet("/config/permissions", (string? dir) =>
        {
            var workingDir = NormalizeDir(dir);
            var path = ProjectConfigPath(workingDir);
            PermissionConfig? cfg = null;
            if (File.Exists(path))
            {
                try { cfg = PermissionConfig.Parse(File.ReadAllText(path)); }
                catch (Exception ex) when (ex is JsonException or ArgumentException or IOException)
                {
                    return Results.Ok(new PermissionsView([], [], [], [],
                        [.. PermissionConfig.KnownCapabilities.OrderBy(x => x, StringComparer.Ordinal)],
                        BuiltinToolNames, path, Error: ex.Message));
                }
            }
            return Results.Ok(new PermissionsView(
                AllowTools: cfg?.AllowTools?.ToArray() ?? [],
                DenyTools: cfg?.DenyTools?.ToArray() ?? [],
                Allow: cfg?.Allow?.Select(r => new RuleView(r.Capability, r.Pattern)).ToArray() ?? [],
                Deny: cfg?.Deny?.Select(r => new RuleView(r.Capability, r.Pattern)).ToArray() ?? [],
                KnownCapabilities: [.. PermissionConfig.KnownCapabilities.OrderBy(x => x, StringComparer.Ordinal)],
                BuiltinTools: BuiltinToolNames,
                ConfigPath: path,
                Error: null));
        });

        app.MapPut("/config/permissions", async (UpdatePermissionsRequest? body) =>
        {
            if (body is null) return Results.BadRequest(new { message = "Body is required." });
            var workingDir = NormalizeDir(body.Dir);
            var path = ProjectConfigPath(workingDir);

            // Merge into the existing document so hooks / mcp / lsp sections survive a permissions edit.
            JsonObject root;
            try
            {
                root = File.Exists(path)
                    ? JsonNode.Parse(await File.ReadAllTextAsync(path))?.AsObject() ?? new JsonObject()
                    : new JsonObject();
            }
            catch (JsonException) { root = new JsonObject(); }

            root["allowTools"] = ToJsonArray(body.AllowTools);
            root["denyTools"] = ToJsonArray(body.DenyTools);
            root["allow"] = ToRuleArray(body.Allow);
            root["deny"] = ToRuleArray(body.Deny);

            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            // Validate before persisting so we never write an unparseable / unknown-capability config.
            try { PermissionConfig.Parse(json); }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                return Results.BadRequest(new { message = ex.Message });
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json);
            return Results.Ok(new { configPath = path });
        });

        app.MapGet("/config/trust", (string? dir) =>
        {
            var workingDir = NormalizeDir(dir);
            var path = ProjectConfigPath(workingDir);
            PermissionConfig? cfg = null;
            if (File.Exists(path))
            {
                try { cfg = PermissionConfig.Parse(File.ReadAllText(path)); }
                catch { /* a broken config simply has nothing trustworthy to describe */ }
            }
            var requests = cfg is null ? [] : ProjectTrust.DescribeRequests(cfg);
            return Results.Ok(new TrustView(
                WorkingDirectory: workingDir,
                Trusted: ProjectTrust.IsTrusted(workingDir),
                Requests: [.. requests]));
        });

        app.MapPost("/config/trust", (TrustRequest? body) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Dir))
                return Results.BadRequest(new { message = "Field 'dir' is required." });
            var workingDir = NormalizeDir(body.Dir);
            ProjectTrust.Trust(workingDir);
            return Results.Ok(new { workingDirectory = workingDir, trusted = true });
        });
    }

    /// <summary>Built-in tool names, surfaced to the UI as suggestions for allow/deny rules.</summary>
    private static readonly string[] BuiltinToolNames =
    [
        "ReadFile", "WriteFile", "EditFile", "MultiEditFile", "ApplyPatch", "ProcessExec",
        "Glob", "Grep", "CSharpAnalysis", "EnterPlanMode", "ExitPlanMode", "ReadMemory",
        "WriteMemory", "ReadArtifact", "SpawnAgent",
    ];

    /// <summary>Maps a provider id to the wire API its models are cataloged under.</summary>
    internal static string? WireApiFor(string? provider) => provider?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "anthropic" or "bedrock" or "vertex" => "anthropic",
        "azure" or "ollama" => "openai",
        var p => p, // "openai" -> "openai"
    };

    private static string? HintFor(string? key) =>
        string.IsNullOrWhiteSpace(key) || key.Length < 4 ? null : "…" + key[^4..];

    private static string NormalizeDir(string? dir) =>
        string.IsNullOrWhiteSpace(dir) ? Directory.GetCurrentDirectory() : Path.GetFullPath(dir);

    /// <summary>Path of a project's config (<c>$FREEAGENT_CONFIG</c> or <c>&lt;dir&gt;/.freeagent/config.json</c>).</summary>
    private static string ProjectConfigPath(string workingDir)
    {
        var path = Environment.GetEnvironmentVariable("FREEAGENT_CONFIG");
        return string.IsNullOrWhiteSpace(path) ? Path.Combine(workingDir, ".freeagent", "config.json") : path;
    }

    private static Dictionary<string, string> ExistingSection(string? existingJson, string provider)
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(existingJson)) return answers;
        try
        {
            if (JsonNode.Parse(existingJson)?.AsObject() is { } root
                && root[provider] is JsonObject section)
            {
                foreach (var (key, value) in section)
                    if (value is not null) answers[key] = value.ToString();
            }
        }
        catch (JsonException) { /* fall through with an empty section */ }
        return answers;
    }

    private static void ApplyField(Dictionary<string, string> answers, string slot, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) answers[slot] = value.Trim();
    }

    private static JsonArray ToJsonArray(IEnumerable<string>? items) =>
        [.. (items ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => JsonValue.Create(s.Trim()))];

    private static JsonArray ToRuleArray(IEnumerable<RuleView>? rules)
    {
        var arr = new JsonArray();
        foreach (var r in rules ?? [])
        {
            if (string.IsNullOrWhiteSpace(r.Capability)) continue;
            var obj = new JsonObject { ["capability"] = r.Capability.Trim() };
            if (!string.IsNullOrWhiteSpace(r.Pattern)) obj["pattern"] = r.Pattern.Trim();
            arr.Add(obj);
        }
        return arr;
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────────────────────
public sealed record ProviderInfo(string Id, string Description, bool RequiresApiKey, ProviderField[] Fields);
public sealed record ProviderField(string Slot, string Label, string? Default, bool Secret);
public sealed record ModelInfo(string Id, string WireApi, int? ContextTokens, int? MaxOutputTokens,
    bool SupportsTools, bool SupportsVision, bool SupportsThinking);
public sealed record ProviderView(string? BaseUrl, string? Model, string? ApiVersion, bool ApiKeySet, string? ApiKeyHint);
public sealed record ConfigView(string ActiveProvider, string ConfigPath, IReadOnlyDictionary<string, ProviderView> Providers);
public sealed record UpdateProviderRequest(string Provider, string? ApiKey, string? BaseUrl, string? Model, string? ApiVersion, bool? SetAsDefault);
public sealed record TestProviderRequest(string Provider, string? ApiKey, string? BaseUrl, string? Model, string? ApiVersion);
public sealed record RuleView(string Capability, string? Pattern);
public sealed record PermissionsView(string[] AllowTools, string[] DenyTools, RuleView[] Allow, RuleView[] Deny,
    string[] KnownCapabilities, string[] BuiltinTools, string ConfigPath, string? Error);
public sealed record UpdatePermissionsRequest(string? Dir, string[]? AllowTools, string[]? DenyTools, RuleView[]? Allow, RuleView[]? Deny);
public sealed record TrustView(string WorkingDirectory, bool Trusted, string[] Requests);
public sealed record TrustRequest(string Dir);
