using System.Diagnostics;
using System.Text;
using FreeAgent.Kernel;

namespace FreeAgent.Host;

/// <summary>
/// Host-side <see cref="IMcpTransport"/> over a child process's stdin/stdout (the MCP "stdio
/// transport"). The process is launched with <see cref="ProcessStartInfo"/>, stdin/stdout are line-
/// delimited UTF-8, and <see cref="Dispose"/> kills the process tree.
/// </summary>
public sealed class StdioMcpTransport : IMcpTransport
{
    private readonly Process _process;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public StdioMcpTransport(string command, IReadOnlyList<string>? args = null, IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args ?? Array.Empty<string>())
            psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env)
                psi.Environment[k] = v;

        _process = new Process { StartInfo = psi };
        _process.Start();

        // Drain stderr in the background so it doesn't fill the OS buffer and block the server.
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync() is { } line)
                    Console.Error.WriteLine($"[mcp:{command}] {line}");
            }
            catch { /* process exited */ }
        });
    }

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        return await _process.StandardOutput.ReadLineAsync(cancellationToken);
    }

    public async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        finally { _writeGate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        try { _process.StandardInput.Dispose(); } catch { }
        _process.Dispose();
        _writeGate.Dispose();
    }
}
