using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FreeAgent.Kernel;

/// <summary>
/// Shared Roslyn glue for the semantic actions of <see cref="CSharpAnalysisTool"/>: builds a
/// <see cref="CSharpCompilation"/> over the workspace's <c>.cs</c> files using the host's runtime
/// assemblies as metadata references. The reference set comes from <c>TRUSTED_PLATFORM_ASSEMBLIES</c>,
/// which already lives in memory — no per-call directory walks of the SDK install. Cached after the
/// first build so subsequent calls are cheap.
/// </summary>
/// <remarks>
/// Limitation: the metadata reference set is the host's assembly graph, not the workspace's
/// project file. For workspace-local symbol queries this is fine — references to user code bind
/// through `SyntaxTree`s — but a reference into a NuGet package the host doesn't ship won't bind.
/// A future iteration could parse `.csproj` to pull in project-specific package references.
/// </remarks>
internal static class RoslynSemanticHelpers
{
    private static readonly object _refsGate = new();
    private static List<MetadataReference>? _cachedRefs;

    public static List<MetadataReference> RuntimeReferences()
    {
        if (_cachedRefs is { } existing) return existing;
        lock (_refsGate)
        {
            if (_cachedRefs is { } existingInLock) return existingInLock;

            var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (string.IsNullOrEmpty(tpa))
            {
                _cachedRefs = [];
                return _cachedRefs;
            }

            var refs = new List<MetadataReference>(capacity: 128);
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                try { refs.Add(MetadataReference.CreateFromFile(path)); }
                catch (Exception ex) when (ex is IOException or BadImageFormatException) { /* skip */ }
            }
            _cachedRefs = refs;
            return _cachedRefs;
        }
    }

    public static CSharpCompilation BuildWorkspaceCompilation(IReadOnlyList<string> sourceFiles, CancellationToken cancellationToken)
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
        return CSharpCompilation.Create(
            "FreeAgentWorkspace",
            trees,
            RuntimeReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
