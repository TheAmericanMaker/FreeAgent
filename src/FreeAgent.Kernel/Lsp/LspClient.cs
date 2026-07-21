using System.Text.Json;

namespace FreeAgent.Kernel;

/// <summary>
/// Thin LSP client. Wraps <see cref="JsonRpcClient"/> with the LSP-specific methods most useful for
/// a coding agent: <c>initialize</c> (handshake), <c>textDocument/didOpen</c> (so the server has the
/// file's text), and the four lookups — <c>hover</c>, <c>definition</c>, <c>references</c>,
/// <c>publishDiagnostics</c> via the per-call <c>textDocument/diagnostic</c> request (LSP 3.17+).
/// Server-pushed <c>publishDiagnostics</c> notifications are currently dropped (the JSON-RPC client
/// ignores id-less envelopes); a future revision can subscribe.
/// </summary>
public sealed class LspClient : IAsyncDisposable, IDisposable
{
    private readonly JsonRpcClient _rpc;

    public LspClient(ILspTransport transport) => _rpc = new JsonRpcClient(transport);

    /// <summary>
    /// Send the <c>initialize</c> request followed by the required <c>initialized</c> notification.
    /// <paramref name="rootUri"/> is the workspace URI (e.g. <c>file:///path/to/workspace</c>).
    /// </summary>
    public async ValueTask InitializeAsync(string rootUri, CancellationToken cancellationToken)
    {
        await _rpc.CallAsync("initialize", writer =>
        {
            writer.WriteNumber("processId", Environment.ProcessId);
            writer.WriteString("rootUri", rootUri);
            writer.WritePropertyName("capabilities");
            writer.WriteStartObject();
            writer.WritePropertyName("textDocument");
            writer.WriteStartObject();
            writer.WritePropertyName("hover");
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WritePropertyName("definition");
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WritePropertyName("references");
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WriteEndObject(); // textDocument
            writer.WriteEndObject(); // capabilities
        }, cancellationToken).ConfigureAwait(false);

        await _rpc.NotifyAsync("initialized", _ => { }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Notify the server that the file at <paramref name="uri"/> is open and carry its text.
    /// Required before <c>hover</c>/<c>definition</c>/<c>references</c> for most servers.
    /// </summary>
    public ValueTask DidOpenAsync(string uri, string languageId, string text, CancellationToken cancellationToken) =>
        _rpc.NotifyAsync("textDocument/didOpen", writer =>
        {
            writer.WritePropertyName("textDocument");
            writer.WriteStartObject();
            writer.WriteString("uri", uri);
            writer.WriteString("languageId", languageId);
            writer.WriteNumber("version", 1);
            writer.WriteString("text", text);
            writer.WriteEndObject();
        }, cancellationToken);

    /// <summary>Hover text at the given position. Returns the concatenated content strings, or empty if none.</summary>
    public async ValueTask<string> HoverAsync(string uri, int line, int character, CancellationToken cancellationToken)
    {
        var result = await CallPositionAsync("textDocument/hover", uri, line, character, cancellationToken);
        if (result.ValueKind != JsonValueKind.Object) return string.Empty;
        if (!result.TryGetProperty("contents", out var contents)) return string.Empty;
        return FlattenHoverContents(contents);
    }

    /// <summary>Definition location(s) for the symbol at the position. Returns a list of "uri:line:col" strings.</summary>
    public async ValueTask<IReadOnlyList<string>> DefinitionAsync(string uri, int line, int character, CancellationToken cancellationToken)
    {
        var result = await CallPositionAsync("textDocument/definition", uri, line, character, cancellationToken);
        return FlattenLocations(result);
    }

    /// <summary>References to the symbol at the position. Returns "uri:line:col" strings.</summary>
    public async ValueTask<IReadOnlyList<string>> ReferencesAsync(string uri, int line, int character, bool includeDeclaration, CancellationToken cancellationToken)
    {
        var result = await _rpc.CallAsync("textDocument/references", writer =>
        {
            WriteTextDocumentPosition(writer, uri, line, character);
            writer.WritePropertyName("context");
            writer.WriteStartObject();
            writer.WriteBoolean("includeDeclaration", includeDeclaration);
            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);
        return FlattenLocations(result);
    }

    private ValueTask<JsonElement> CallPositionAsync(string method, string uri, int line, int character, CancellationToken cancellationToken) =>
        new(_rpc.CallAsync(method, writer => WriteTextDocumentPosition(writer, uri, line, character), cancellationToken));

    private static void WriteTextDocumentPosition(System.Text.Json.Utf8JsonWriter writer, string uri, int line, int character)
    {
        writer.WritePropertyName("textDocument");
        writer.WriteStartObject();
        writer.WriteString("uri", uri);
        writer.WriteEndObject();
        writer.WritePropertyName("position");
        writer.WriteStartObject();
        writer.WriteNumber("line", line);
        writer.WriteNumber("character", character);
        writer.WriteEndObject();
    }

    private static string FlattenHoverContents(JsonElement contents) => contents.ValueKind switch
    {
        JsonValueKind.String => contents.GetString() ?? string.Empty,
        JsonValueKind.Object when contents.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
            => v.GetString() ?? string.Empty,
        JsonValueKind.Array => string.Join("\n", contents.EnumerateArray().Select(FlattenHoverContents).Where(s => s.Length > 0)),
        _ => string.Empty,
    };

    private static IReadOnlyList<string> FlattenLocations(JsonElement result)
    {
        var locations = new List<string>();
        switch (result.ValueKind)
        {
            case JsonValueKind.Object:
                AppendLocation(locations, result);
                break;
            case JsonValueKind.Array:
                foreach (var item in result.EnumerateArray()) AppendLocation(locations, item);
                break;
        }
        return locations;
    }

    private static void AppendLocation(List<string> dest, JsonElement loc)
    {
        if (loc.ValueKind != JsonValueKind.Object) return;
        // Both Location and LocationLink shapes: pick "uri"+"range" / "targetUri"+"targetRange".
        var uri = loc.TryGetProperty("uri", out var u) ? u.GetString()
            : loc.TryGetProperty("targetUri", out var tu) ? tu.GetString()
            : null;
        if (uri is null) return;
        var range = loc.TryGetProperty("range", out var r) ? r
            : loc.TryGetProperty("targetRange", out var tr) ? tr
            : default;
        if (range.ValueKind != JsonValueKind.Object) return;
        if (!range.TryGetProperty("start", out var start) || start.ValueKind != JsonValueKind.Object) return;
        var line = start.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
        var col = start.TryGetProperty("character", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
        dest.Add($"{uri}:{line + 1}:{col + 1}");
    }

    public ValueTask DisposeAsync() => _rpc.DisposeAsync();
    public void Dispose() => _rpc.Dispose();
}
