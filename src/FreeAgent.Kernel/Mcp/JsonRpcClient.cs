using System.Text;
using System.Text.Json;

namespace FreeAgent.Kernel;

/// <summary>
/// Minimal JSON-RPC 2.0 client over an <see cref="IJsonRpcTransport"/>: serial outgoing IDs, a
/// background read loop dispatches responses to waiting <see cref="TaskCompletionSource{TResult}"/>s
/// by id. Used by <see cref="McpClient"/> and <see cref="LspClient"/>; reusable for any peer that
/// speaks JSON-RPC 2.0 envelopes (the transport handles framing).
/// </summary>
public sealed class JsonRpcClient : IAsyncDisposable, IDisposable
{
    private readonly IJsonRpcTransport _transport;
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _readLoop;
    private int _nextId;
    private int _disposed;

    public JsonRpcClient(IJsonRpcTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        // Start without Task.Run: the async loop yields on the first await and continues on the
        // thread pool. Wrapping in Task.Run added a layer that interacted oddly with disposal in
        // the test runner.
        _readLoop = ReadLoopAsync();
    }

    /// <summary>Sends a request and awaits its response (matched by id).</summary>
    public async Task<JsonElement> CallAsync(string method, Action<Utf8JsonWriter>? writeParams, CancellationToken cancellationToken)
    {
        int id;
        TaskCompletionSource<JsonElement> tcs;
        lock (_gate)
        {
            id = ++_nextId;
            tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
        }

        var json = SerializeEnvelope(method, id, writeParams);
        await _transport.WriteLineAsync(json, cancellationToken);

        await using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return await tcs.Task;
    }

    /// <summary>Sends a notification (no id, no response expected).</summary>
    public ValueTask NotifyAsync(string method, Action<Utf8JsonWriter>? writeParams, CancellationToken cancellationToken)
    {
        var json = SerializeEnvelope(method, id: null, writeParams);
        return _transport.WriteLineAsync(json, cancellationToken);
    }

    private static string SerializeEnvelope(string method, int? id, Action<Utf8JsonWriter>? writeParams)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        if (id is { } actualId)
            writer.WriteNumber("id", actualId);
        writer.WriteString("method", method);
        if (writeParams is not null)
        {
            writer.WritePropertyName("params");
            writer.WriteStartObject();
            writeParams(writer);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private async Task ReadLoopAsync()
    {
        // Detach from any captured context — xUnit's async machinery interacts poorly with
        // a background-running async loop that captures the test method's context.
        await Task.Yield();

        try
        {
            while (await _transport.ReadLineAsync(_shutdown.Token).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number)
                        continue; // notification / server-pushed — ignored for v1

                    var id = idProp.GetInt32();
                    TaskCompletionSource<JsonElement>? tcs;
                    lock (_gate) { _pending.Remove(id, out tcs); }
                    if (tcs is null) continue;

                    if (root.TryGetProperty("error", out var err))
                    {
                        var code = err.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
                        var msg = err.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                        tcs.TrySetException(new InvalidOperationException($"JSON-RPC error {code}: {msg}"));
                    }
                    else if (root.TryGetProperty("result", out var result))
                    {
                        tcs.TrySetResult(result.Clone());
                    }
                    else
                    {
                        tcs.TrySetResult(default);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            FailAllPending(ex);
        }
        finally
        {
            FailAllPending(new InvalidOperationException("JSON-RPC transport closed."));
        }
    }

    private void FailAllPending(Exception ex)
    {
        lock (_gate)
        {
            foreach (var (_, t) in _pending)
                t.TrySetException(ex);
            _pending.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Cancel + dispose the transport up front so the read loop's ReadLineAsync unblocks via
        // whichever path wins (cancellation token or transport EOF). Then wait for it to finish.
        _shutdown.Cancel();
        _transport.Dispose();
        try { await _readLoop.ConfigureAwait(false); } catch { /* swallow */ }
        _shutdown.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
