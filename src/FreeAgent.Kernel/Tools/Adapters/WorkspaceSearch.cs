using System.Text;
using System.Text.RegularExpressions;

namespace FreeAgent.Kernel;

/// <summary>
/// Shared file-walking and glob-matching helpers for the read-only search tools
/// (<see cref="GlobTool"/>, <see cref="GrepTool"/>). Walks deterministically (sorted), skips a small
/// set of noise directories that are almost never search targets and would otherwise dominate the
/// traversal, and translates a glob to an anchored regex with the usual <c>**</c> / <c>*</c> / <c>?</c>
/// semantics. Paths are matched with <c>/</c> separators relative to the search root.
/// </summary>
internal static class WorkspaceSearch
{
    private static readonly HashSet<string> NoiseDirectories = new(StringComparer.Ordinal)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea"
    };

    /// <summary>
    /// Lazily yields every file under <paramref name="root"/> (recursive), skipping noise directories
    /// and silently stepping over directories the process cannot read. Enumeration is sorted at each
    /// level for deterministic output.
    /// </summary>
    public static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] subdirs;
            string[] files;
            try
            {
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
                yield return file;

            // Push in reverse-sorted order so the stack pops them in ascending order.
            Array.Sort(subdirs, StringComparer.Ordinal);
            for (var i = subdirs.Length - 1; i >= 0; i--)
            {
                var name = System.IO.Path.GetFileName(subdirs[i]);
                if (!NoiseDirectories.Contains(name))
                    stack.Push(subdirs[i]);
            }
        }
    }

    /// <summary>Path of <paramref name="file"/> relative to <paramref name="root"/>, using <c>/</c> separators.</summary>
    public static string RelativePath(string root, string file) =>
        System.IO.Path.GetRelativePath(root, file).Replace(System.IO.Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Compiles a glob to an anchored regex. <c>**/</c> matches zero or more path segments, <c>**</c>
    /// matches across separators, <c>*</c> matches within a segment, <c>?</c> matches one non-separator
    /// character. Everything else is matched literally.
    /// </summary>
    public static Regex CompileGlob(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant);
    }
}
