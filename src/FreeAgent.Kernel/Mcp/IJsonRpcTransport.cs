namespace FreeAgent.Kernel;

/// <summary>
/// One-JSON-envelope-at-a-time read/write seam for <see cref="JsonRpcClient"/>. Framing is the
/// implementation's job — MCP uses newline-delimited JSON (one envelope per line); LSP uses
/// <c>Content-Length</c>-headered framing; an in-memory fake might use a queue. The client only
/// sees the unframed envelope text.
/// </summary>
public interface IJsonRpcTransport : IDisposable
{
    /// <summary>Reads the next JSON envelope (without any framing). Returns null on EOF.</summary>
    ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken);

    /// <summary>Writes <paramref name="line"/> with whatever framing the transport requires. Flushes.</summary>
    ValueTask WriteLineAsync(string line, CancellationToken cancellationToken);
}

/// <summary>
/// MCP-specific alias for <see cref="IJsonRpcTransport"/>. Kept for the MCP layer's call sites so
/// the type name reads as the protocol the transport is carrying.
/// </summary>
public interface IMcpTransport : IJsonRpcTransport { }

/// <summary>
/// LSP-specific alias for <see cref="IJsonRpcTransport"/>. Same wire-level contract as
/// <see cref="IMcpTransport"/>; the LSP transport is responsible for handling
/// <c>Content-Length</c>-headered framing on read and write.
/// </summary>
public interface ILspTransport : IJsonRpcTransport { }
