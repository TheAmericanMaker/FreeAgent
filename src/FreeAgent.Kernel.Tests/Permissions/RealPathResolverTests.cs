using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Permissions;

public sealed class RealPathResolverTests
{
    [Fact]
    public void FollowsASymlinkedDirectoryComponentToItsRealTarget()
    {
        var root = Directory.CreateTempSubdirectory("freeagent-realpath-");
        try
        {
            var inside = Directory.CreateDirectory(Path.Combine(root.FullName, "inside"));
            var outside = Directory.CreateDirectory(Path.Combine(root.FullName, "outside"));
            var link = Path.Combine(inside.FullName, "link");
            Directory.CreateSymbolicLink(link, outside.FullName);

            var resolver = new RealPathResolver();

            // A path *under* the symlinked directory resolves to the real (outside) location — the same
            // place resolving the real path directly lands. (Compared self-consistently so a symlinked
            // temp root, e.g. macOS /tmp, doesn't make the assertion brittle.)
            var viaLink = resolver.Resolve(Path.Combine(link, "secret.txt"));
            var direct = resolver.Resolve(Path.Combine(outside.FullName, "secret.txt"));

            viaLink.Should().Be(direct);
            viaLink.Should().NotContain($"{Path.DirectorySeparatorChar}link");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void AppendsANonExistingRemainderLexically()
    {
        var root = Directory.CreateTempSubdirectory("freeagent-realpath-");
        try
        {
            var resolver = new RealPathResolver();
            var realRoot = resolver.Resolve(root.FullName); // the existing, resolved base
            var path = Path.Combine(root.FullName, "newdir", "new.txt"); // remainder doesn't exist

            resolver.Resolve(path).Should().Be(Path.Combine(realRoot, "newdir", "new.txt"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
