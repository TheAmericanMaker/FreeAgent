namespace FreeAgent.Kernel;

/// <summary>
/// Resolves a filesystem path to its real, symlink-followed location. <see cref="ToolPipeline"/> uses
/// it to canonicalize capability paths (and the working directory) before the pure, lexical
/// <see cref="PermissionEngine"/> decides — so a symlink <em>inside</em> the workspace can't smuggle a
/// read/write to a target outside it, or through a hardcoded protected prefix. Behind an interface so
/// the pipeline stays testable against a fake with no real filesystem, and so the engine keeps its
/// no-I/O purity (ADR 0004).
/// </summary>
public interface IRealPathResolver
{
    /// <summary>
    /// Returns the real path of <paramref name="absolutePath"/>: symlinks in the longest existing
    /// prefix are followed; any non-existing remainder (e.g. a file about to be created) is appended
    /// lexically. Best-effort and must never throw — on any error it returns the lexical full path.
    /// </summary>
    string Resolve(string absolutePath);
}
