namespace FreeAgent.Kernel;

/// <summary>
/// Default <see cref="IRealPathResolver"/>: walks the path one component at a time, following symlinks
/// (bounded against loops) over the portion that exists and appending any non-existing remainder
/// lexically. So <c>/wd/link/file</c> where <c>/wd/link → /etc</c> resolves to <c>/etc/file</c>, which
/// the permission engine then sees as outside the workspace / under a protected prefix. Linux-native
/// first (ADR 0003); relies only on the BCL's <see cref="FileSystemInfo.LinkTarget"/>.
/// </summary>
public sealed class RealPathResolver : IRealPathResolver
{
    // Guards against a symlink cycle (a → b → a) sending the walk into an infinite loop.
    private const int MaxSymlinkHops = 40;

    public string Resolve(string absolutePath)
    {
        try
        {
            var full = Path.GetFullPath(absolutePath);
            var separator = Path.DirectorySeparatorChar;
            var parts = full.Split(separator, StringSplitOptions.RemoveEmptyEntries);
            var current = separator.ToString();

            foreach (var part in parts)
            {
                var candidate = Path.Combine(current, part);

                var hops = 0;
                while (hops++ < MaxSymlinkHops)
                {
                    FileSystemInfo? info =
                        Directory.Exists(candidate) ? new DirectoryInfo(candidate)
                        : File.Exists(candidate) ? new FileInfo(candidate)
                        : null;

                    // Non-existing component, or a plain (non-symlink) entry: nothing more to follow.
                    if (info?.LinkTarget is not { } target)
                        break;

                    // A relative link target resolves against the directory the link lives in.
                    var linkDir = Path.GetDirectoryName(candidate) ?? current;
                    candidate = Path.GetFullPath(target, linkDir);
                }

                current = candidate;
            }

            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Best-effort: a resolution failure falls back to the lexical full path.
            return Path.GetFullPath(absolutePath);
        }
    }
}
