using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Persistence;

public sealed class LinuxAtomicFileSystemTests
{
    [Fact]
    public async Task JsonlSessionStoreDefaultsToRealAtomicFileSystemAndWritesTargetFile()
    {
        var directory = Directory.CreateTempSubdirectory("freeagent-jsonl-");
        var path = Path.Combine(directory.FullName, "session.jsonl");
        var store = new JsonlSessionStore(path: path);
        var state = new SessionState("session-1", directory.FullName, DateTimeOffset.Parse("2026-05-25T00:00:00Z"));
        state.Messages.Add(new Message(MessageRole.User, "hello"));

        await store.SaveAsync(state, CancellationToken.None);

        File.Exists(path).Should().BeTrue();
        var jsonl = await File.ReadAllTextAsync(path, CancellationToken.None);
        jsonl.Should().EndWith("\n");
    }

    [Fact]
    public async Task TempPathsAreUniqueSameDirectoryAndNotFixedTargetTmpNames()
    {
        var directory = Directory.CreateTempSubdirectory("freeagent-jsonl-");
        var targetPath = Path.Combine(directory.FullName, "session.jsonl");
        var fs = new LinuxAtomicFileSystem();

        var first = await fs.CreateTempPathAsync(targetPath, CancellationToken.None);
        var second = await fs.CreateTempPathAsync(targetPath, CancellationToken.None);

        first.Should().NotBe(second);
        Path.GetDirectoryName(first).Should().Be(directory.FullName);
        Path.GetDirectoryName(second).Should().Be(directory.FullName);
        first.Should().NotBe(targetPath + ".tmp");
        second.Should().NotBe(targetPath + ".tmp");
    }
}
