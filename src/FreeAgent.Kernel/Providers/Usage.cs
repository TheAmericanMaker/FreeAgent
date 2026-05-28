namespace FreeAgent.Kernel;

/// <summary>
/// Normalized provider token usage. Optional cache fields are populated by providers that expose
/// them (Anthropic <c>cache_read_input_tokens</c>/<c>cache_creation_input_tokens</c>; OpenAI's
/// <c>prompt_tokens_details.cached_tokens</c>). Adding new fields here is additive — existing
/// `new Usage(input, output)` constructions still compile.
/// </summary>
public sealed record Usage(int InputTokens, int OutputTokens, int CacheReadTokens = 0, int CacheWriteTokens = 0);
