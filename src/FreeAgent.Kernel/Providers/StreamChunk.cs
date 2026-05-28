namespace FreeAgent.Kernel;

/// <summary>
/// One slice of a provider's streaming response. A turn is the concatenation of many of these:
/// most chunks carry either a text/thinking delta or a tool-call delta; the last one usually
/// carries usage and <c>IsComplete=true</c>. <see cref="StopReason"/> is optional — providers fill
/// it when they know, and the runtime uses it to distinguish "out of tokens" from "naturally done".
/// </summary>
public sealed record StreamChunk(
    string? ThinkingDelta = null,
    string? TextDelta = null,
    ToolCallDelta? ToolCallDelta = null,
    Usage? Usage = null,
    bool IsComplete = false,
    StopReason StopReason = StopReason.Unknown);
