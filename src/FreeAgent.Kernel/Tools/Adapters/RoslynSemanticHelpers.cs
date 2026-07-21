using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FreeAgent.Kernel;

/// <summary>
/// Shared Roslyn glue for the semantic actions of <see cref="CSharpAnalysisTool"/>: builds a
/// <see cref="CSharpCompilation"/> over the workspace's <c>.cs</c> files using two reference sets:
/// <list type="bullet">
///   <item>The host's runtime assemblies via <c>TRUSTED_PLATFORM_ASSEMBLIES</c> — covers the .NET
///         stdlib for free without an SDK directory walk.</item>
///   <item>Per-project NuGet references resolved from any <c>obj/project.assets.json</c> files
///         under the working directory — covers the packages each <c>.csproj</c> actually pulls in
///         (after a successful <c>dotnet restore</c>).</item>
/// </list>
/// Both sets are cached after the first build. The workspace cache is keyed by working directory
/// so multiple workspaces don't tread on each other.
/// </summary>
public static class RoslynSemanticHelpers
{
    private static readonly object _refsGate = new();
    private static List<MetadataReference>? _cachedRuntimeRefs;

    private static readonly object _workspaceCacheGate = new();
    private static readonly Dictionary<string, IReadOnlyList<MetadataReference>> _workspaceRefsByDir = new(StringComparer.Ordinal);

    /// <summary>
    /// The host's runtime assemblies — every DLL listed in <c>TRUSTED_PLATFORM_ASSEMBLIES</c>. These
    /// already live in memory; <see cref="MetadataReference.CreateFromFile(string)"/> just mmaps the
    /// PE. Cached once per process.
    /// </summary>
    public static IReadOnlyList<MetadataReference> RuntimeReferences()
    {
        if (_cachedRuntimeRefs is { } existing) return existing;
        lock (_refsGate)
        {
            if (_cachedRuntimeRefs is { } existingInLock) return existingInLock;

            var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (string.IsNullOrEmpty(tpa))
            {
                _cachedRuntimeRefs = [];
                return _cachedRuntimeRefs;
            }

            var refs = new List<MetadataReference>(capacity: 128);
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                try { refs.Add(MetadataReference.CreateFromFile(path)); }
                catch (Exception ex) when (ex is IOException or BadImageFormatException) { /* skip */ }
            }
            _cachedRuntimeRefs = refs;
            return _cachedRuntimeRefs;
        }
    }

    /// <summary>
    /// NuGet references resolved from every <c>obj/project.assets.json</c> under
    /// <paramref name="workingDirectory"/>. Empty if no such files exist (e.g. before
    /// <c>dotnet restore</c>) or if every candidate package DLL is missing on disk. Cached per
    /// working directory.
    /// </summary>
    public static IReadOnlyList<MetadataReference> WorkspacePackageReferences(string workingDirectory)
    {
        var normalized = Path.GetFullPath(workingDirectory);
        lock (_workspaceCacheGate)
        {
            if (_workspaceRefsByDir.TryGetValue(normalized, out var cached))
                return cached;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<MetadataReference>();
        foreach (var assetsFile in EnumerateAssetsFiles(normalized))
        {
            try
            {
                var json = File.ReadAllText(assetsFile);
                foreach (var absoluteDll in ResolveAssetsReferences(json))
                {
                    if (!seen.Add(absoluteDll)) continue;
                    try { refs.Add(MetadataReference.CreateFromFile(absoluteDll)); }
                    catch (Exception ex) when (ex is IOException or BadImageFormatException) { /* skip */ }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Best-effort: a malformed assets file shouldn't break the whole analysis.
            }
        }

        lock (_workspaceCacheGate)
        {
            _workspaceRefsByDir[normalized] = refs;
        }
        return refs;
    }

    /// <summary>
    /// Walks <see cref="EnumerateAssetsFiles"/> output looking for <c>obj/project.assets.json</c>.
    /// Skips noise dirs (<c>.git</c>, <c>node_modules</c>, <c>bin</c>, …) and bounds depth so a
    /// huge tree can't run away. <c>obj</c> is intentionally NOT in the skip set — that's where
    /// the asset file lives.
    /// </summary>
    public static IEnumerable<string> EnumerateAssetsFiles(string root)
    {
        const int MaxDepth = 10;
        var noiseDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "node_modules", "bin", ".vs", ".idea"
        };

        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            if (depth > MaxDepth) continue;

            string[] files;
            string[] subdirs;
            try
            {
                files = Directory.GetFiles(dir);
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var f in files)
            {
                if (string.Equals(Path.GetFileName(f), "project.assets.json", StringComparison.Ordinal))
                    yield return f;
            }
            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (noiseDirs.Contains(name)) continue;
                queue.Enqueue((sub, depth + 1));
            }
        }
    }

    /// <summary>
    /// Parses a <c>project.assets.json</c> document and yields the absolute paths of every
    /// reference assembly NuGet resolved for it. Pure — no I/O beyond <see cref="File.Exists"/>
    /// probes, which are required to disambiguate between the (typically only) configured
    /// <c>packageFolders</c>. Exposed for testing.
    /// </summary>
    public static IEnumerable<string> ResolveAssetsReferences(string assetsJson)
    {
        using var doc = JsonDocument.Parse(assetsJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("packageFolders", out var pf) || pf.ValueKind != JsonValueKind.Object)
            yield break;
        var packageFolders = new List<string>();
        foreach (var folder in pf.EnumerateObject())
            packageFolders.Add(folder.Name.TrimEnd('/', '\\'));
        if (packageFolders.Count == 0) yield break;

        if (!root.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var tfm in targets.EnumerateObject())
        {
            foreach (var pkg in tfm.Value.EnumerateObject())
            {
                // pkg.Name is "PackageId/Version"; NuGet stores the package as lowercase on disk.
                var key = pkg.Name.ToLowerInvariant();
                if (pkg.Value.ValueKind != JsonValueKind.Object) continue;
                if (!pkg.Value.TryGetProperty("compile", out var compile) || compile.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var dll in compile.EnumerateObject())
                {
                    var relative = dll.Name.Replace('\\', '/');
                    if (!relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

                    foreach (var folder in packageFolders)
                    {
                        var absolute = Path.Combine(folder, key, relative);
                        if (File.Exists(absolute))
                        {
                            yield return absolute;
                            break;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Builds a compilation that merges the runtime reference set with any workspace package
    /// references found under <paramref name="workingDirectory"/>. Pass an empty string (or the
    /// process working dir) to skip workspace resolution.
    /// </summary>
    public static CSharpCompilation BuildWorkspaceCompilation(
        IReadOnlyList<string> sourceFiles,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var trees = new List<SyntaxTree>(sourceFiles.Count);
        foreach (var file in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var text = File.ReadAllText(file);
                trees.Add(CSharpSyntaxTree.ParseText(text, path: file, cancellationToken: cancellationToken));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* skip unreadable */ }
        }

        var refs = new List<MetadataReference>(RuntimeReferences());
        if (!string.IsNullOrEmpty(workingDirectory))
            refs.AddRange(WorkspacePackageReferences(workingDirectory));

        return CSharpCompilation.Create(
            "FreeAgentWorkspace",
            trees,
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
