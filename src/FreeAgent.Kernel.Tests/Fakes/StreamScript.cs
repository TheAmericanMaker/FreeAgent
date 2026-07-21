using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests;

public static class StreamScript
{
    public static IReadOnlyList<StreamChunk> Text(string text) => [new(TextDelta: text, IsComplete: true)];
    public static IReadOnlyList<StreamChunk> ToolCall(string id, string name, string argumentsJson) => [new(ToolCallDelta: new ToolCallDelta(id, name, argumentsJson), IsComplete: true)];
    public static IReadOnlyList<StreamChunk> Mixed(string? thinking, string? text, Usage? usage) => [new(ThinkingDelta: thinking, TextDelta: text, Usage: usage, IsComplete: true)];

    // ── Individual-chunk helpers for partial delta tests ────────────────────

    public static StreamChunk T(string text) => new(TextDelta: text);
    public static StreamChunk Delta(string id, string name, string argumentsJson) => new(ToolCallDelta: new ToolCallDelta(id, name, argumentsJson));
    public static StreamChunk Done() => new(IsComplete: true);
    public static IReadOnlyList<StreamChunk> Script(params StreamChunk[] chunks) => chunks;
}
