namespace FreeAgent.Kernel;

/// <summary>
/// Newline-delimited JSON transport (read + write) — the seam that lets the kernel-side
/// <see cref="JsonRpcClient"/> drive an MCP server without knowing whether it's a subprocess, a
/// socket, or an in-memory fake.
/// </summary>
public interface IMcpTransport : IDisposable
{
    /// <summary>Reads the next line (without the newline). Returns null on EOF.</summary>
    ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken);

    /// <summary>Writes <paramref name="line"/> followed by a newline. Flushes.</summary>
    ValueTask WriteLineAsync(string line, CancellationToken cancellationToken);
}
