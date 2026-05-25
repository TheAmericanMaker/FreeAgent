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
        return _count == _threshold;
    }

    private static string NormalizeJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement, JsonOptions.Default);
    }
}
