using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeAgent.Kernel;

/// <summary>
/// Declarative permission rules loaded from a config file and applied to a <see cref="PermissionEngine"/>
/// at startup, so writes / extra binaries / etc. can be granted without code changes. Capability rules
/// name a <see cref="Capability"/> subtype; a rule with no <see cref="CapabilityRuleConfig.Pattern"/>
/// (or <c>"*"</c>) covers the whole type, otherwise the pattern is matched against the capability's
/// target with the engine's glob semantics. Hardcoded security blocks (e.g. <c>sudo</c>, <c>/etc</c>)
/// still cannot be overridden by an allow rule.
/// </summary>
public sealed class PermissionConfig
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>All <see cref="Capability"/> subtype names, discovered once via reflection.</summary>
    public static readonly IReadOnlySet<string> KnownCapabilities =
        typeof(Capability).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false } && t.IsSubclassOf(typeof(Capability)))
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

    [JsonPropertyName("allowTools")] public IReadOnlyList<string>? AllowTools { get; init; }
    [JsonPropertyName("denyTools")] public IReadOnlyList<string>? DenyTools { get; init; }
    [JsonPropertyName("allow")] public IReadOnlyList<CapabilityRuleConfig>? Allow { get; init; }
    [JsonPropertyName("deny")] public IReadOnlyList<CapabilityRuleConfig>? Deny { get; init; }

    public sealed record CapabilityRuleConfig(string Capability, string? Pattern = null);

    /// <summary>Parses and validates a config document. Throws <see cref="JsonException"/> on malformed
    /// JSON and <see cref="ArgumentException"/> on an unknown capability name.</summary>
    public static PermissionConfig Parse(string json)
    {
        var config = JsonSerializer.Deserialize<PermissionConfig>(json, JsonOpts)
            ?? throw new JsonException("Permission config deserialized to null.");
        config.Validate();
        return config;
    }

    /// <summary>Total number of rules, for diagnostics.</summary>
    public int RuleCount =>
        (AllowTools?.Count ?? 0) + (DenyTools?.Count ?? 0) + (Allow?.Count ?? 0) + (Deny?.Count ?? 0);

    public void Validate()
    {
        foreach (var rule in (Allow ?? []).Concat(Deny ?? []))
        {
            if (string.IsNullOrWhiteSpace(rule.Capability))
                throw new ArgumentException("A permission rule is missing its 'capability' name.");
            if (!KnownCapabilities.Contains(rule.Capability))
                throw new ArgumentException(
                    $"Unknown capability '{rule.Capability}'. Known capabilities: {string.Join(", ", KnownCapabilities.OrderBy(x => x, StringComparer.Ordinal))}.");
        }
    }

    public void ApplyTo(PermissionEngine engine)
    {
        foreach (var tool in AllowTools ?? []) engine.AllowTool(tool);
        foreach (var tool in DenyTools ?? []) engine.DenyTool(tool);

        foreach (var rule in Allow ?? [])
        {
            if (IsWholeType(rule.Pattern)) engine.AllowCapabilityType(rule.Capability);
            else engine.AllowCapabilityRule(rule.Capability, rule.Pattern!);
        }

        foreach (var rule in Deny ?? [])
        {
            if (IsWholeType(rule.Pattern)) engine.DenyCapabilityType(rule.Capability);
            else engine.DenyCapabilityRule(rule.Capability, rule.Pattern!);
        }
    }

    private static bool IsWholeType(string? pattern) => string.IsNullOrEmpty(pattern) || pattern == "*";
}
