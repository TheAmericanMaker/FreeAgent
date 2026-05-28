using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Tools.Adapters;

public sealed class RoslynSemanticHelpersTests
{
    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "freeagent-roslyn-helper-tests", Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void RuntimeReferencesIncludesTheStdlib()
    {
        var refs = RoslynSemanticHelpers.RuntimeReferences();
        refs.Should().NotBeEmpty("the host's TPA always has at least System.Private.CoreLib");
    }

    [Fact]
    public void EnumerateAssetsFilesFindsObjProjectAssetsJson()
    {
        using var tmp = new TempDir();
        // Mirror what dotnet restore creates: <repo>/<project>/obj/project.assets.json
        var projDir = Path.Combine(tmp.Root, "MyApp");
        var objDir = Path.Combine(projDir, "obj");
        Directory.CreateDirectory(objDir);
        var assets = Path.Combine(objDir, "project.assets.json");
        File.WriteAllText(assets, "{}");

        var found = RoslynSemanticHelpers.EnumerateAssetsFiles(tmp.Root).ToList();

        found.Should().ContainSingle().Which.Should().Be(assets);
    }

    [Fact]
    public void EnumerateAssetsFilesSkipsNoiseDirectories()
    {
        using var tmp = new TempDir();
        // Should be skipped — files under .git / node_modules / bin / .vs / .idea.
        foreach (var noise in new[] { ".git", "node_modules", "bin", ".vs", ".idea" })
        {
            var d = Path.Combine(tmp.Root, noise, "obj");
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "project.assets.json"), "{}");
        }
        // Should be found — real project obj/.
        var real = Path.Combine(tmp.Root, "Real", "obj");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "project.assets.json"), "{}");

        var found = RoslynSemanticHelpers.EnumerateAssetsFiles(tmp.Root).ToList();

        found.Should().ContainSingle().Which.Should().EndWith("Real/obj/project.assets.json".Replace('/', Path.DirectorySeparatorChar));
    }

    [Fact]
    public void ResolveAssetsReferencesYieldsAbsolutePathsForExistingDlls()
    {
        using var tmp = new TempDir();
        // Lay out a fake NuGet packages folder with a single DLL on disk.
        var pkgFolder = Path.Combine(tmp.Root, "packages") + Path.DirectorySeparatorChar;
        var dllDir = Path.Combine(pkgFolder, "fakepackage/1.0.0/lib/net10.0");
        Directory.CreateDirectory(dllDir);
        var dllPath = Path.Combine(dllDir, "FakePackage.dll");
        File.WriteAllBytes(dllPath, []); // empty file is enough — File.Exists is the only probe here.

        var assetsJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 3,
            packageFolders = new Dictionary<string, object> { [pkgFolder] = new { } },
            targets = new Dictionary<string, object>
            {
                ["net10.0"] = new Dictionary<string, object>
                {
                    ["FakePackage/1.0.0"] = new
                    {
                        type = "package",
                        compile = new Dictionary<string, object> { ["lib/net10.0/FakePackage.dll"] = new { } },
                    }
                }
            }
        });

        var resolved = RoslynSemanticHelpers.ResolveAssetsReferences(assetsJson).ToList();

        resolved.Should().ContainSingle().Which.Should().Be(dllPath);
    }

    [Fact]
    public void ResolveAssetsReferencesSkipsDllsThatDoNotExistOnDisk()
    {
        using var tmp = new TempDir();
        var pkgFolder = Path.Combine(tmp.Root, "packages") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(pkgFolder);
        // Note: no DLL file is actually created.

        var assetsJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [pkgFolder] = new { } },
            targets = new Dictionary<string, object>
            {
                ["net10.0"] = new Dictionary<string, object>
                {
                    ["Missing/1.0.0"] = new
                    {
                        compile = new Dictionary<string, object> { ["lib/net10.0/Missing.dll"] = new { } }
                    }
                }
            }
        });

        RoslynSemanticHelpers.ResolveAssetsReferences(assetsJson).Should().BeEmpty();
    }

    [Fact]
    public void ResolveAssetsReferencesIgnoresNonDllCompileEntries()
    {
        // Real assets files often have "_._" sentinel entries; we ignore anything not ending in .dll.
        var assetsJson = """
            {
              "packageFolders": { "/tmp/pkg": {} },
              "targets": {
                "net10.0": {
                  "P/1.0.0": { "compile": { "lib/net10.0/_._": {} } }
                }
              }
            }
            """;
        RoslynSemanticHelpers.ResolveAssetsReferences(assetsJson).Should().BeEmpty();
    }

    [Fact]
    public void WorkspacePackageReferencesReturnsEmptyForNoAssetsFiles()
    {
        using var tmp = new TempDir();
        // Brand-new workspace; no obj/ anywhere.
        RoslynSemanticHelpers.WorkspacePackageReferences(tmp.Root).Should().BeEmpty();
    }
}
