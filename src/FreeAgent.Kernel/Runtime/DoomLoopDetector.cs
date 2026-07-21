using System.Text.Json;

namespace FreeAgent.Kernel;

public sealed class DoomLoopDetector
{
    private readonly int _threshold;
    private string? _lastSignature;
    private int _count;

    public DoomLoopDetector(int threshold) => _threshold = threshold;

    public void Reset()
    {
        _lastSignature = null;
        _count = 0;
    }

    public bool Observe(IReadOnlyList<ToolCall> calls)
    {
        var signature = string.Join("|", calls.Select(c => $"{c.Name}:{NormalizeJson(c.ArgumentsJson)}"));
        _count = signature == _lastSignature ? _count + 1 : 1;
        _lastSignature = signature;
        // >= rather than ==: once the threshold is reached, every further identical batch must keep
        // tripping the guard, otherwise the repeat would silently execute again on the next iteration.
        return _count >= _threshold;
    }

    private static string NormalizeJson(string json)
    {
        // Tool-call arguments arrive accumulated from a stream and may be empty or truncated. The
        // pipeline maps malformed JSON to InvalidInput downstream; here we only need a stable
        // signature, so fall back to the raw text rather than letting a parse error escape the turn.
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, JsonOptions.Default);
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
