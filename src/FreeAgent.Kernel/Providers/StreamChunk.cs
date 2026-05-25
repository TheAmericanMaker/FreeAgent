namespace FreeAgent.Kernel;

public sealed record StreamChunk(
    string? ThinkingDelta = null,
    string? TextDelta = null,
    ToolCallDelta? ToolCallDelta = null,
    Usage? Usage = null,
    bool IsComplete = false);
