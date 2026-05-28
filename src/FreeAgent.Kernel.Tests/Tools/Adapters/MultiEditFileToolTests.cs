using System.Text.Json;
using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Tools.Adapters;

public sealed class MultiEditFileToolTests
{
    private static JsonDocument Args(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value));
    private static ToolContext Context(string workingDirectory) =>
        new(new SessionState("m", workingDirectory, DateTimeOffset.UnixEpoch));

    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "freeagent-tests", Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task SuccessfulBatchAppliesAllEditsAtomicallyAndSnapshots()
    {
        using var work = new TempWorkspace();
        var file = Path.Combine(work.Root, "a.txt");
        await File.WriteAllTextAsync(file, "alpha beta gamma");
        var context = Context(work.Root);

        var result = await new MultiEditFileTool().ExecuteAsync(
            Args(new
            {
                path = "a.txt",
                edits = new[]
                {
                    new { old_string = "alpha", new_string = "A" },
                    new { old_string = "gamma", new_string = "C" },
                }
            }), context, CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Contain("2 edit(s)").And.Contain("2 replacement(s)");
        (await File.ReadAllTextAsync(file)).Should().Be("A beta C");
        context.Session.History.Count.Should().Be(1); // single snapshot for the whole batch
    }

    [Fact]
    public async Task AnyFailingEditAbortsTheEntireBatchAndWritesNothing()
    {
        using var work = new TempWorkspace();
        var file = Path.Combine(work.Root, "a.txt");
        await File.WriteAllTextAsync(file, "alpha beta");
        var context = Context(work.Root);

        var result = await new MultiEditFileTool().ExecuteAsync(
            Args(new
            {
                path = "a.txt",
                edits = new object[]
                {
                    new { old_string = "alpha", new_string = "A" },
                    new { old_string = "missing", new_string = "X" }, // fails
                }
            }), context, CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
        result.Content.Should().Contain("Edit #1").And.Contain("not found");
        (await File.ReadAllTextAsync(file)).Should().Be("alpha beta"); // unchanged
        context.Session.History.Count.Should().Be(0); // no snapshot since no write
    }

    [Fact]
    public async Task AmbiguousMatchInBatchIsRejectedUnlessReplaceAll()
    {
        using var work = new TempWorkspace();
        var file = Path.Combine(work.Root, "a.txt");
        await File.WriteAllTextAsync(file, "x x x");
        var context = Context(work.Root);

        var rejected = await new MultiEditFileTool().ExecuteAsync(
            Args(new
            {
                path = "a.txt",
                edits = new[] { new { old_string = "x", new_string = "y" } }
            }), context, CancellationToken.None);
        rejected.Kind.Should().Be(ToolResultKind.InvalidInput);
        rejected.Content.Should().Contain("occurs 3 times");

        var batched = await new MultiEditFileTool().ExecuteAsync(
            Args(new
            {
                path = "a.txt",
                edits = new[]
                {
                    new { old_string = "x", new_string = "y", replace_all = true },
                }
            }), context, CancellationToken.None);
        batched.Kind.Should().Be(ToolResultKind.Success);
        (await File.ReadAllTextAsync(file)).Should().Be("y y y");
    }

    [Fact]
    public async Task SequentialEditsSeeEachOthersChanges()
    {
        using var work = new TempWorkspace();
        var file = Path.Combine(work.Root, "a.txt");
        await File.WriteAllTextAsync(file, "alpha");
        var context = Context(work.Root);

        // Second edit's old_string only exists after the first edit applied — verifies in-memory chaining.
        var result = await new MultiEditFileTool().ExecuteAsync(
            Args(new
            {
                path = "a.txt",
                edits = new[]
                {
                    new { old_string = "alpha", new_string = "beta" },
                    new { old_string = "beta",  new_string = "gamma" },
                }
            }), context, CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        (await File.ReadAllTextAsync(file)).Should().Be("gamma");
    }

    [Fact]
    public async Task EmptyEditsArrayIsInvalidInput()
    {
        using var work = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(work.Root, "a.txt"), "x");
        var result = await new MultiEditFileTool().ExecuteAsync(
            Args(new { path = "a.txt", edits = Array.Empty<object>() }),
            Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
    }
}
