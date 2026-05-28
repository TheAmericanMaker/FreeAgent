using System.Text.Json;
using System.Text.Json.Serialization;
using FreeAgent.Kernel;

namespace FreeAgent.Host;

/// <summary>
/// Interactive console implementation of <see cref="IPermissionApprover"/>. When the kernel needs
/// approval for an uncovered capability, it prompts the user <c>[once / session / always / deny]</c>.
/// "always" appends an allow rule (by capability type) to the project's <c>.freeagent/config.json</c>
/// so the grant persists across runs, and is also treated as a session grant for the current run.
/// Prompts are serialized so concurrent (parallel-window) tool calls cannot interleave on the console.
/// </summary>
public sealed class ConsoleApprover : IPermissionApprover
{
    private readonly string _workingDir;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ConsoleApprover(string workingDir) => _workingDir = workingDir;

    public async ValueTask<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return ApprovalDecision.Deny;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Console.WriteLine();
            Console.WriteLine($"⚠ '{request.ToolName}' needs approval for:");
            foreach (var capability in request.Capabilities)
                Console.WriteLine($"    • {capability.Describe()}");
            Console.Write("  [o]nce  [s]ession  [a]lways (save rule)  [d]eny › ");

            var answer = Console.In.ReadLine()?.Trim().ToLowerInvariant();
            switch (answer)
            {
                case "o" or "once":
                    return ApprovalDecision.Once;
                case "s" or "session":
                    return ApprovalDecision.Session;
                case "a" or "always":
                    TryPersistAllowRules(request.Capabilities);
                    return ApprovalDecision.Session;
                default:
                    return ApprovalDecision.Deny;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns <paramref name="existing"/> with a whole-type allow rule added for each capability
    /// type not already covered. Pure (no I/O) so it is unit-tested directly.
    /// </summary>
    public static PermissionConfig MergeAllowRules(PermissionConfig existing, IReadOnlyList<Capability> capabilities)
    {
        var allow = new List<PermissionConfig.CapabilityRuleConfig>(existing.Allow ?? []);
        foreach (var capability in capabilities)
        {
            var name = capability.GetType().Name;
            var alreadyCovered = allow.Any(r =>
                string.Equals(r.Capability, name, StringComparison.Ordinal) && string.IsNullOrEmpty(r.Pattern));
            if (!alreadyCovered)
                allow.Add(new PermissionConfig.CapabilityRuleConfig(name));
        }

        return new PermissionConfig
        {
            AllowTools = existing.AllowTools,
            DenyTools = existing.DenyTools,
            Allow = allow,
            Deny = existing.Deny
        };
    }

    private void TryPersistAllowRules(IReadOnlyList<Capability> capabilities)
    {
        var path = Path.Combine(_workingDir, ".freeagent", "config.json");
        try
        {
            var existing = File.Exists(path) ? PermissionConfig.Parse(File.ReadAllText(path)) : new PermissionConfig();
            var merged = MergeAllowRules(existing, capabilities);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(merged, WriteOptions));
            Console.WriteLine($"  ✓ saved allow rule(s) to {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"  (could not save to {path}: {ex.Message}; granted for this session only)");
        }
    }
}
