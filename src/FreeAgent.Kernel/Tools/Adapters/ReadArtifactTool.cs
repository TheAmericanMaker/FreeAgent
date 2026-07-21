using System.Text.Json;

namespace FreeAgent.Kernel;

/// <summary>
/// Retrieves the full content of an artifact the pipeline offloaded to the
/// <see cref="IArtifactStore"/>. Read-only, no capability required — the artifact was already
/// produced by an authorized tool and lives in the session's own store.
/// </summary>
public sealed class ReadArtifactTool : ITool
{
    private readonly IArtifactStore _store;

    public ReadArtifactTool(IArtifactStore store) => _store = store;

    public string Name => "ReadArtifact";

    public string Description =>
        "Fetch the full text of an artifact that a previous tool call offloaded to the artifact "
        + "store (its result reported a `ref=...` and a preview). Required: 'ref'.";

    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonDocument InputSchema { get; } = JsonDocument.Parse(
        """{"type":"object","required":["ref"],"properties":{"ref":{"type":"string"}}}""");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonDocument arguments, ToolContext context) => [];

    public ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var reference = arguments.RootElement.GetProperty("ref").GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(reference))
            return ValueTask.FromResult(ToolResult.InvalidInput("'ref' must not be empty."));

        return _store.TryGet(reference, out var content)
            ? ValueTask.FromResult(ToolResult.Success(content))
            : ValueTask.FromResult(ToolResult.InvalidInput($"Unknown artifact ref '{reference}'."));
    }
}
