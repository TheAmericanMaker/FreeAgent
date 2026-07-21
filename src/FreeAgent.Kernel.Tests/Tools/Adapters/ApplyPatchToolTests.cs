using System.Text.Json;
using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Tools.Adapters;

public sealed class ApplyPatchToolTests
{
    private static JsonDocument Args(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value));
    private static ToolContext Context(string workingDirectory) =>
        new(new SessionState("p", workingDirectory, DateTimeOffset.UnixEpoch));

    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "freeagent-tests", Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    // ── ParseHunks (pure) ────────────────────────────────────────────────────

    [Fact]
    public void ParseHunksSplitsAddedAndRemovedAroundContext()
    {
        var patch = """
            --- a/file
            +++ b/file
            @@ -1,3 +1,3 @@
             one
            -two
            +TWO
             three
            """;

        var hunks = ApplyPatchTool.ParseHunks(patch);

        hunks.Should().ContainSingle();
        hunks[0].OldText.Should().Be("one\ntwo\nthree");
        hunks[0].NewText.Should().Be("one\nTWO\nthree");
    }

    [Fact]
    public void ParseHunksHandlesMultipleHunks()
    {
        var patch = """
            @@ -1 +1 @@
            -a
            +A
            @@ -3 +3 @@
            -c
            +C
            """;

        var hunks = ApplyPatchTool.ParseHunks(patch);

        hunks.Should().HaveCount(2);
        hunks[0].OldText.Should().Be("a");
        hunks[1].NewText.Should().Be("C");
    }

    // ── Tool behavior ────────────────────────────────────────────────────────

    [Fact]
    public async Task AppliesSingleHunkAndSnapshotsForUndo()
    {
        using var work = new TempWorkspace();
        var file = Path.Combine(work.Root, "a.txt");
        await File.WriteAllTextAsync(file, "one\ntwo\nthree\n");
        var context = Context(work.Root);

        var result = await new ApplyPatchTool().ExecuteAsync(
            Args(new
            {
                path = "a.txt",
                patch = "@@ -1,3 +1,3 @@\n one\n-two\n+TWO\n three\n"
            }),
            context, CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        (await File.ReadAllTextAsync(file)).Should().Contain("TWO");
        context.Session.History.Count.Should().Be(1);
    }

    [Fact]
    public async Task UnmatchedHunkAbortsTheEntirePatch()
    {
        using var work = new TempWorkspace();
        var file = Path.Combine(work.Root, "a.txt");
        await File.WriteAllTextAsync(file, "alpha\nbeta\n");
        var context = Context(work.Root);

        var result = await new ApplyPatchTool().ExecuteAsync(
            Args(new
            {
                path = "a.txt",
                patch = "@@ -1 +1 @@\n-missing\n+new\n"
            }),
            context, CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
        result.Content.Should().Contain("did not match");
        (await File.ReadAllTextAsync(file)).Should().Be("alpha\nbeta\n");
        context.Session.History.Count.Should().Be(0);
    }

    [Fact]
    public async Task EmptyPatchIsInvalidInput()
    {
        using var work = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(work.Root, "a.txt"), "x");

        var result = await new ApplyPatchTool().ExecuteAsync(
            Args(new { path = "a.txt", patch = "" }), Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
    }

    [Fact]
    public async Task MultipleHunksAreAppliedInOrder()
    {
        using var work = new TempWorkspace();
        var file = Path.Combine(work.Root, "a.txt");
        await File.WriteAllTextAsync(file, "alpha\nbeta\ngamma\n");
        var context = Context(work.Root);

        var patch = "@@ -1 +1 @@\n-alpha\n+ALPHA\n@@ -3 +3 @@\n-gamma\n+GAMMA\n";
        var result = await new ApplyPatchTool().ExecuteAsync(
            Args(new { path = "a.txt", patch }), context, CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Contain("2 hunk(s)");
        (await File.ReadAllTextAsync(file)).Should().Be("ALPHA\nbeta\nGAMMA\n");
    }

    [Fact]
    public async Task MissingFileReturnsInvalidInput()
    {
        using var work = new TempWorkspace();

        var result = await new ApplyPatchTool().ExecuteAsync(
            Args(new { path = "absent.txt", patch = "@@ -1 +1 @@\n-a\n+b\n" }),
            Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
    }
}
