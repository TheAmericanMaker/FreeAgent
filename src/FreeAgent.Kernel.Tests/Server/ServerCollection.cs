using Xunit;

namespace FreeAgent.Kernel.Tests.Server;

/// <summary>
/// Serializes the FreeAgent.Server HTTP tests. Several of them toggle process-global environment
/// variables (<c>FREEAGENT_SERVER_API_KEY</c>, <c>XDG_CONFIG_HOME</c>, provider keys) and stand up a
/// <c>WebApplicationFactory</c> that reads those at build time — running two such classes in parallel
/// would let one class's env mutation leak into another's server. This collection forces them to run
/// one at a time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ServerCollection
{
    public const string Name = "FreeAgent.Server HTTP tests (non-parallel)";
}
