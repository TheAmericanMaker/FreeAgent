namespace FreeAgent.Kernel;

public interface IPermissionEngine
{
    /// <summary>
    /// Decide whether <paramref name="tool"/> may run given the capabilities it requires.
    /// Deterministic and non-interactive: uncovered capabilities are denied (a later UX layer
    /// can replace that with a prompt). <paramref name="workingDirectory"/> bounds path-based
    /// auto-allow rules.
    /// </summary>
    PermissionDecision Decide(ITool tool, IReadOnlyList<Capability> capabilities, string workingDirectory);
}
