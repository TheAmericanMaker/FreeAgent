namespace FreeAgent.Kernel;

/// <summary>
/// Why a provider's generation ended. Normalized across the major wire APIs: OpenAI's
/// <c>finish_reason</c> ("stop" | "length" | "tool_calls" | "content_filter"), Anthropic's
/// <c>stop_reason</c> ("end_turn" | "max_tokens" | "tool_use" | "stop_sequence" | "refusal"), and
/// Ollama's <c>done_reason</c> (when set — usually absent). Knowing the reason lets the runtime
/// distinguish "model is finished" from "ran out of tokens before finishing", which changes how the
/// next iteration should be framed.
/// </summary>
public enum StopReason
{
    /// <summary>The provider didn't tell us, or we couldn't map what it said.</summary>
    Unknown = 0,

    /// <summary>Natural end of the turn — model produced a final reply.</summary>
    EndTurn,

    /// <summary>Model wants to call tools; the runtime should execute them and continue.</summary>
    ToolUse,

    /// <summary>Hit the per-request token budget (OpenAI "length" / Anthropic "max_tokens").</summary>
    MaxTokens,

    /// <summary>Generation matched a configured stop sequence.</summary>
    StopSequence,

    /// <summary>Content policy refused the generation.</summary>
    Refusal,
}
