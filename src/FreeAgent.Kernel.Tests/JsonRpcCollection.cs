using Xunit;

namespace FreeAgent.Kernel.Tests;

/// <summary>
/// xUnit forces test classes in the same collection to run sequentially. We use this for the MCP
/// and LSP end-to-end smoke tests because both wire a <c>JsonRpcClient</c> with a background read
/// loop over an in-memory transport; running them in parallel hits an interaction where one test's
/// disposed read loop can race the other's start. Sequencing them with this collection lets both
/// pass cleanly.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class JsonRpcCollection
{
    public const string Name = "JsonRpc smoke tests (non-parallel)";
}
