using System.Text.RegularExpressions;

namespace FreeAgent.Kernel;

/// <summary>
/// Deterministic, non-interactive capability permission engine. Evaluation order:
/// <list type="number">
///   <item>Hardcoded security blocks (blocked binaries, protected write paths) — never overridable.</item>
///   <item>Tool-level deny — beats any allow.</item>
///   <item>Capability-level deny (type or rule) — beats any allow.</item>
///   <item>Empty capability list — allow.</item>
///   <item>Tool-level allow — covers all of the tool's capabilities.</item>
///   <item>Per-capability coverage: allowed type, allow rule, or auto-allow.</item>
///   <item>Any uncovered capability — deny (a UX layer would prompt here).</item>
/// </list>
/// Mirrors contracts §"Permission and Authorization Model".
/// </summary>
public sealed class PermissionEngine : IPermissionEngine
{
    // Binaries blocked regardless of allow rules.
    private static readonly HashSet<string> BlockedBinaries = new(StringComparer.OrdinalIgnoreCase)
    {
        "sudo", "su", "doas", "pkexec", "chmod", "chown", "chattr", "setfacl", "icacls", "takeown", "attrib"
    };

    // Write path prefixes blocked regardless of allow rules.
    private static readonly string[] ProtectedWritePrefixes =
    {
        "/etc/", "/usr/", "/bin/", "/sbin/", "/System/", "/Library/"
    };

    // Read-only binaries that are auto-allowed.
    private static readonly HashSet<string> SafeReadOnlyBinaries = new(StringComparer.Ordinal)
    {
        "pwd", "ls", "cat", "head", "tail", "grep", "rg", "find"
    };

    // Read-only git subcommands that are auto-allowed (binary "git" + this first arg).
    private static readonly HashSet<string> SafeGitSubcommands = new(StringComparer.Ordinal)
    {
        "status", "diff", "log"
    };

    private readonly HashSet<string> _deniedTools = new(StringComparer.Ordinal);
    private readonly HashSet<string> _allowedTools = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deniedCapabilityTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _allowedCapabilityTypes = new(StringComparer.Ordinal);
    private readonly List<CapabilityRule> _denyRules = [];
    private readonly List<CapabilityRule> _allowRules = [];

    public void DenyTool(string name) => _deniedTools.Add(name);
    public void AllowTool(string name) => _allowedTools.Add(name);

    public void DenyCapabilityType<TCapability>() where TCapability : Capability =>
        _deniedCapabilityTypes.Add(typeof(TCapability).Name);

    public void AllowCapabilityType<TCapability>() where TCapability : Capability =>
        _allowedCapabilityTypes.Add(typeof(TCapability).Name);

    public void DenyCapabilityRule<TCapability>(string pattern) where TCapability : Capability =>
        _denyRules.Add(new CapabilityRule(typeof(TCapability).Name, pattern));

    public void AllowCapabilityRule<TCapability>(string pattern) where TCapability : Capability =>
        _allowRules.Add(new CapabilityRule(typeof(TCapability).Name, pattern));

    // Name-keyed overloads for config-driven rules. The engine matches capabilities by their type
    // name internally, so these store the same way as the generic forms; callers (e.g.
    // PermissionConfig) are responsible for validating that the name is a real capability type.
    public void AllowCapabilityType(string capabilityTypeName) => _allowedCapabilityTypes.Add(capabilityTypeName);
    public void DenyCapabilityType(string capabilityTypeName) => _deniedCapabilityTypes.Add(capabilityTypeName);
    public void AllowCapabilityRule(string capabilityTypeName, string pattern) => _allowRules.Add(new CapabilityRule(capabilityTypeName, pattern));
    public void DenyCapabilityRule(string capabilityTypeName, string pattern) => _denyRules.Add(new CapabilityRule(capabilityTypeName, pattern));

    public PermissionDecision Decide(ITool tool, IReadOnlyList<Capability> capabilities, string workingDirectory)
    {
        // 1. Hardcoded security blocks — apply even if an allow rule or AllowTool is set.
        foreach (var capability in capabilities)
        {
            switch (capability)
            {
                case ProcessExecCap exec when IsBlockedBinary(exec.Binary):
                    return PermissionDecision.Deny(
                        $"Blocked binary: {BinaryName(exec.Binary)} (never permitted, regardless of allow rules).");
                case FileWriteCap write when IsProtectedPath(write.Path, workingDirectory, out var prefix):
                    return PermissionDecision.Deny(
                        $"Protected path: writes under '{prefix}' are not permitted ({write.Path}).");
            }
        }

        // 2. Tool-level deny beats any allow.
        if (_deniedTools.Contains(tool.Name))
        {
            return PermissionDecision.Deny($"Tool '{tool.Name}' is denied for this session.");
        }

        // 3. Capability-level deny (type or rule) beats any allow.
        foreach (var capability in capabilities)
        {
            if (_deniedCapabilityTypes.Contains(capability.GetType().Name) || MatchesAny(_denyRules, capability))
            {
                return PermissionDecision.Deny($"Capability denied by session rule: {capability.Describe()}.");
            }
        }

        // 4. No capabilities required → allow (the tool was not denied above).
        if (capabilities.Count == 0)
        {
            return PermissionDecision.Allow();
        }

        // 5. Tool-level allow covers all of this tool's capabilities.
        if (_allowedTools.Contains(tool.Name))
        {
            return PermissionDecision.Allow();
        }

        // 6. Every capability must be covered by an allowed type, an allow rule, or auto-allow.
        foreach (var capability in capabilities)
        {
            if (_allowedCapabilityTypes.Contains(capability.GetType().Name)) continue;
            if (MatchesAny(_allowRules, capability)) continue;
            if (IsAutoAllowed(capability, workingDirectory)) continue;

            return PermissionDecision.Deny(
                $"Capability requires approval: {capability.Describe()}. No auto-allow rule matched.",
                retryHint: "A UX layer must prompt the user to approve this capability, or add an allow rule.");
        }

        // 7. All capabilities covered.
        return PermissionDecision.Allow();
    }

    private static bool IsAutoAllowed(Capability capability, string workingDirectory) => capability switch
    {
        FileReadCap read => IsInsideWorkingDirectory(read.Path, workingDirectory),
        MemoryCap memory => string.Equals(memory.Operation, "read", StringComparison.Ordinal),
        ProcessExecCap exec => IsSafeReadOnlyBinary(exec),
        _ => false
    };

    private static bool IsSafeReadOnlyBinary(ProcessExecCap exec)
    {
        var name = BinaryName(exec.Binary);
        if (SafeReadOnlyBinaries.Contains(name)) return true;
        return string.Equals(name, "git", StringComparison.Ordinal)
            && exec.Args.Count >= 1
            && SafeGitSubcommands.Contains(exec.Args[0]);
    }

    private static bool IsBlockedBinary(string binary) => BlockedBinaries.Contains(BinaryName(binary));

    private static string BinaryName(string binary)
    {
        var name = Path.GetFileName(binary);
        return string.IsNullOrEmpty(name) ? binary : name;
    }

    private static bool IsInsideWorkingDirectory(string path, string workingDirectory)
    {
        var root = Path.GetFullPath(workingDirectory);
        var full = Path.GetFullPath(path, root);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return full == root || full.StartsWith(rootWithSeparator, StringComparison.Ordinal);
    }

    private static bool IsProtectedPath(string path, string workingDirectory, out string matchedPrefix)
    {
        var full = Path.GetFullPath(path, Path.GetFullPath(workingDirectory));
        foreach (var prefix in ProtectedWritePrefixes)
        {
            if (full.StartsWith(prefix, StringComparison.Ordinal))
            {
                matchedPrefix = prefix;
                return true;
            }
        }

        matchedPrefix = string.Empty;
        return false;
    }

    private static bool MatchesAny(List<CapabilityRule> rules, Capability capability)
    {
        var typeName = capability.GetType().Name;
        foreach (var rule in rules)
        {
            if (rule.CapabilityType == typeName && GlobMatch(rule.Pattern, capability.MatchTarget))
            {
                return true;
            }
        }

        return false;
    }

    // Simple, anchored, case-insensitive glob: '*' → any run, '?' → one char.
    private static bool GlobMatch(string pattern, string value)
    {
        if (pattern == "*") return true;
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
    }

    private sealed record CapabilityRule(string CapabilityType, string Pattern);
}
