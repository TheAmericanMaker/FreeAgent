namespace FreeAgent.Kernel;

/// <summary>
/// Pure context-window compaction. When the last reported input-token count crosses a fraction of
/// the configured context window, the runtime drops older *turns* (full <see cref="MessageRole.User"/>
/// → <see cref="MessageRole.Assistant"/> blocks) while preserving leading <see cref="MessageRole.System"/>
/// messages, the last <c>K</c> turns, and the model-side conversation invariants (tool_use →
/// tool_result pairings, alternation: the first kept non-system message is User). A notice is
/// prepended to the first kept user message so the model knows context was trimmed.
/// <para>
/// This is the safety net so long sessions don't overrun the window; an LLM-based "summarize the
/// dropped turns" pass can layer on later and replace the notice with a real summary.
/// </para>
/// </summary>
public static class Compactor
{
    public const double DefaultThreshold = 0.8;
    public const int DefaultKeepTurns = 4;

    /// <summary>True when <paramref name="lastInputTokens"/> exceeds <paramref name="threshold"/> of <paramref name="contextWindow"/>.</summary>
    public static bool ShouldCompact(int lastInputTokens, int contextWindow, double threshold = DefaultThreshold) =>
        contextWindow > 0 && lastInputTokens > (int)(contextWindow * threshold);

    /// <summary>
    /// Returns a new message list with older turns removed; or a copy of the input unchanged if
    /// there are at most <paramref name="keepLastTurns"/> turns to begin with. Pure (no I/O).
    /// </summary>
    public static List<Message> Compact(IReadOnlyList<Message> messages, int keepLastTurns = DefaultKeepTurns)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keepLastTurns);

        // 1. Leading System block stays intact.
        var systemEnd = 0;
        while (systemEnd < messages.Count && messages[systemEnd].Role == MessageRole.System)
            systemEnd++;

        // 2. Turn boundaries (User messages) in the rest.
        var turnStarts = new List<int>();
        for (var i = systemEnd; i < messages.Count; i++)
        {
            if (messages[i].Role == MessageRole.User)
                turnStarts.Add(i);
        }

        if (turnStarts.Count <= keepLastTurns)
            return [.. messages];

        var keepStart = turnStarts[^keepLastTurns];
        var droppedMessages = keepStart - systemEnd;
        var notice = $"[Compacted: {droppedMessages} earlier message(s) were removed to stay within the context window.]\n\n";

        var result = new List<Message>(messages.Count - droppedMessages);
        for (var i = 0; i < systemEnd; i++)
            result.Add(messages[i]);

        // Prepend the notice to the first kept user message (preserves user-starts-the-conversation
        // alternation that providers like Anthropic require).
        var first = messages[keepStart];
        result.Add(first with { Content = notice + (first.Content ?? string.Empty) });
        for (var i = keepStart + 1; i < messages.Count; i++)
            result.Add(messages[i]);

        return result;
    }
}
