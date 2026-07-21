namespace FreeAgent.Kernel;

/// <summary>
/// In-memory catalog of <see cref="Model"/> records keyed by <c>wire-api/id</c>. Pure (no I/O); the
/// host populates it from its config or hard-coded defaults and passes it down. Designed for
/// additive lookup — callers ask "what do I know about <c>anthropic/claude-3-7-sonnet-latest</c>?"
/// and get back the record (with context window, default budgets, feature flags) if it's been
/// registered, or null. Nothing in the runtime depends on a model being registered; the catalog
/// just unlocks better defaults when it is.
/// </summary>
public sealed class ModelCatalog
{
    private readonly Dictionary<string, Model> _models = new(StringComparer.Ordinal);

    /// <summary>Register a model; overwrites a prior registration under the same wire-api/id pair.</summary>
    public ModelCatalog Register(Model model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _models[Key(model.WireApi, model.Id)] = model;
        return this;
    }

    /// <summary>Resolve a model by <see cref="Model.WireApi"/> + <see cref="Model.Id"/>. Returns null if not registered.</summary>
    public Model? TryResolve(string wireApi, string id) =>
        _models.TryGetValue(Key(wireApi, id), out var m) ? m : null;

    /// <summary>Enumerate all registered models.</summary>
    public IReadOnlyList<Model> All => [.. _models.Values];

    private static string Key(string wireApi, string id) => $"{wireApi}/{id}";

    /// <summary>
    /// Built-in defaults for the providers shipped today. Conservative: only entries we can
    /// state with confidence; for everything else the catalog returns null and the runtime uses
    /// its general fallbacks (the model still works, it just doesn't get specialized handling).
    /// </summary>
    public static ModelCatalog Defaults()
    {
        var c = new ModelCatalog();

        // Anthropic
        c.Register(new Model(
            Id: "claude-3-7-sonnet-latest",
            WireApi: "anthropic",
            ContextTokens: 200_000,
            DefaultMaxOutputTokens: 8_192,
            SupportsTools: true,
            SupportsVision: true,
            SupportsThinking: true));
        c.Register(new Model(
            Id: "claude-3-5-haiku-latest",
            WireApi: "anthropic",
            ContextTokens: 200_000,
            DefaultMaxOutputTokens: 8_192,
            SupportsTools: true,
            SupportsVision: true));

        // OpenAI
        c.Register(new Model(
            Id: "gpt-4o",
            WireApi: "openai",
            ContextTokens: 128_000,
            DefaultMaxOutputTokens: 16_384,
            SupportsTools: true,
            SupportsVision: true));
        c.Register(new Model(
            Id: "gpt-4o-mini",
            WireApi: "openai",
            ContextTokens: 128_000,
            DefaultMaxOutputTokens: 16_384,
            SupportsTools: true,
            SupportsVision: true));

        return c;
    }
}
