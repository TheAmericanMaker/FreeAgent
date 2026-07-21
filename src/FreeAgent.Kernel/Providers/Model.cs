namespace FreeAgent.Kernel;

/// <summary>
/// Capability and limits metadata for a specific model identifier. Optional — providers don't have
/// to publish a model record, but when they do the runtime can size budgets, gate features (vision,
/// tool use, thinking) and pick sensible defaults without hard-coding model strings throughout the
/// codebase. Mirrors the shape the open-source agent community settled on after
/// going through the "hardcoded gpt-4o-mini default" trap.
/// </summary>
public sealed record Model(
    /// <summary>Provider-issued model identifier (e.g. <c>claude-3-7-sonnet-latest</c>).</summary>
    string Id,
    /// <summary>The wire API the model speaks. One of <c>openai</c>, <c>anthropic</c>, <c>ollama</c>, etc.</summary>
    string WireApi,
    /// <summary>Maximum context tokens, if known. <c>null</c> when the provider doesn't publish it.</summary>
    int? ContextTokens = null,
    /// <summary>Default per-request max-output-tokens ceiling, if known.</summary>
    int? DefaultMaxOutputTokens = null,
    /// <summary>Whether the model supports function/tool calls.</summary>
    bool SupportsTools = true,
    /// <summary>Whether the model supports vision input.</summary>
    bool SupportsVision = false,
    /// <summary>Whether the model supports extended reasoning / thinking blocks.</summary>
    bool SupportsThinking = false);
