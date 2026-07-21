namespace FreeAgent.Kernel;

public sealed record Message(
    MessageRole Role,
    string Content,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? ToolName = null,
    DateTimeOffset? Timestamp = null);
