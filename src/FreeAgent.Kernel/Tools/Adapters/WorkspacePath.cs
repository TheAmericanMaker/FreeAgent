namespace FreeAgent.Kernel;

/// <summary>
/// Resolves a tool's user-supplied path against the session working directory using exactly the
/// same rule <see cref="PermissionEngine"/> applies to capability paths, so the capability a tool
/// declares at step 5 and the path it acts on at step 8 always agree. An absolute input is returned
/// normalized; a relative input is taken relative to the working directory. Deterministic: the
/// result depends only on the two inputs, never on the process's current directory.
/// </summary>
internal static class WorkspacePath
{
    public static string Resolve(string path, string workingDirectory) =>
        Path.GetFullPath(path, Path.GetFullPath(workingDirectory));
}
