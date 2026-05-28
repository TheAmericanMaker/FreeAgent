using System.Diagnostics;
using System.Text;
using FreeAgent.Kernel;

namespace FreeAgent.Host;

/// <summary>
/// Host-side <see cref="ILspTransport"/> over a child process's stdin/stdout. LSP wraps every
/// JSON-RPC envelope in <c>Content-Length: N\r\n\r\n{body}</c> framing (vscode-jsonrpc); this
/// transport handles the framing in both directions and exposes one envelope at a time to the
/// JSON-RPC client. Stderr is drained into the void on a background task so the server's buffer
/// can't block.
/// </summary>
public sealed class StdioLspTransport : ILspTransport
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly Task _stderrPump;
    private int _disposed;

    public StdioLspTransport(string command, IReadOnlyList<string> args, string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var psi = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            psi.WorkingDirectory = workingDirectory;
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start LSP server '{command}'.");
        _stdin = _process.StandardInput.BaseStream;
        _stdout = _process.StandardOutput.BaseStream;

        _stderrPump = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null)
                {
                    // discard — could route into the host's event sink if we wanted to surface server logs
                }
            }
            catch { /* shutdown */ }
        });
    }

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) return null;

        var contentLength = -1;
        while (true)
        {
            var header = await ReadHeaderLineAsync(cancellationToken).ConfigureAwait(false);
            if (header is null) return null;
            if (header.Length == 0) break;

            var colon = header.IndexOf(':');
            if (colon < 0) continue;
            var name = header[..colon].Trim();
            var value = header[(colon + 1)..].Trim();
            if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, out var parsed))
            {
                contentLength = parsed;
            }
        }

        if (contentLength <= 0) return null;

        var buffer = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var n = await _stdout.ReadAsync(buffer.AsMemory(read, contentLength - read), cancellationToken).ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
        }
        return Encoding.UTF8.GetString(buffer);
    }

    private async Task<string?> ReadHeaderLineAsync(CancellationToken cancellationToken)
    {
        var sb = new StringBuilder(64);
        var oneByte = new byte[1];
        var sawCR = false;
        while (true)
        {
            var n = await _stdout.ReadAsync(oneByte.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (n == 0) return null;
            var c = (char)oneByte[0];
            if (sawCR && c == '\n') return sb.ToString();
            if (c == '\r') { sawCR = true; continue; }
            if (sawCR) { sb.Append('\r'); sawCR = false; }
            sb.Append(c);
        }
    }

    public async ValueTask WriteLineAsync(string message, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        var body = Encoding.UTF8.GetBytes(message);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _stdin.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await _stdin.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { /* already gone */ }
        try { _stderrPump.Wait(TimeSpan.FromMilliseconds(200)); } catch { }
        _process.Dispose();
    }
}
