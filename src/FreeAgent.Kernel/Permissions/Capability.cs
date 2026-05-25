namespace FreeAgent.Kernel;

/// <summary>
/// A fine-grained authorization unit that a tool requires before it may act. The permission
/// engine decides each capability independently. Mirrors contracts §"Permission and
/// Authorization Model" → Capability subtypes.
/// </summary>
public abstract record Capability
{
    /// <summary>The value session allow/deny rules pattern-match against (per the contract).</summary>
    public abstract string MatchTarget { get; }

    /// <summary>Short, model-facing description, e.g. "FileWriteCap(/tmp/work/out.txt)".</summary>
    public string Describe() => $"{GetType().Name}({MatchTarget})";
}

/// <summary>Read a file. Auto-allowed when the path resolves inside the working directory.</summary>
public sealed record FileReadCap(string Path) : Capability
{
    public override string MatchTarget => Path;
}

/// <summary>Write/modify a file. Never auto-allowed; protected path prefixes are always blocked.</summary>
public sealed record FileWriteCap(string Path, string Operation = "modify") : Capability
{
    public override string MatchTarget => Path;
}

/// <summary>Run a process. Safe read-only binaries are auto-allowed; a block list is never allowed.</summary>
public sealed record ProcessExecCap(string Binary, IReadOnlyList<string> Args) : Capability
{
    public override string MatchTarget => Binary;
}

/// <summary>Make an outbound network connection. Never auto-allowed.</summary>
public sealed record NetworkEgressCap(string Host, int Port = 0, string Protocol = "https") : Capability
{
    public override string MatchTarget => Host;
}

/// <summary>Mutate a version-control repository. Never auto-allowed.</summary>
public sealed record VcsMutationCap(string Repo, string Operation) : Capability
{
    public override string MatchTarget => Repo;
}

/// <summary>Access agent memory. Auto-allowed only when the operation is "read".</summary>
public sealed record MemoryCap(string Namespace, string Operation) : Capability
{
    public override string MatchTarget => Namespace;
}

/// <summary>Spawn a sub-agent. Never auto-allowed.</summary>
public sealed record AgentSpawnCap(string AgentType, string TaskSummary) : Capability
{
    public override string MatchTarget => AgentType;
}
