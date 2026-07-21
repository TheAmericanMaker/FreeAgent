using System.Text.Json;
using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Tools.Adapters;

public sealed class EditFileToolTests
{
    private static JsonDocument Args(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value));
    private static ToolContext Context(string workingDirectory) =>
        new(new SessionState("edit-session", workingDirectory, DateTimeOffset.UnixEpoch));

    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "freeagent-tests", Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    [Fact]
    public void FlagsAndCapabilityAreCorrect()
    {
        var tool = new EditFileTool();
        tool.Name.Should().Be("EditFile");
        tool.IsReadOnly.Should().BeFalse();
        tool.IsConcurrencySafe.Should().BeFalse();
        tool.Description.Should().NotBeNullOrWhiteSpace();

        using var work = new TempWorkspace();
        var caps = tool.RequiredCapabilities(
            Args(new { path = "x.txt", old_string = "a", new_string = "b" }),
            Context(work.Root));
        caps.Should().ContainSingle().Which.Should().BeOfType<FileWriteCap>()
            .Which.Path.Should().Be(Path.GetFullPath(Path.Combine(work.Root, "x.txt")));
    }

    [Fact]
    public async Task UniqueMatchIsReplacedAndReportsCount()
    {
        using var work = new TempWorkspace();
        var file = Path.Combine(work.Root, "a.txt");
        await File.WriteAllTextAsync(file, "hello world");
        var tool = new EditFileTool();

        var result = await tool.ExecuteAsync(
            Args(new { path = "a.txt", old_string = "world", new_string = "FreeAgent" }),
            Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Contain("1 replacement");
        (await File.ReadAllTextAsync(file)).Should().Be("hello FreeAgent");
    }

    [Fact]
    public async Task AmbiguousMatchIsRejectedUnlessReplaceAll()
    {
        using var work = new TempWorkspace();
        var file = Path.Combine(work.Root, "a.txt");
        await File.WriteAllTextAsync(file, "x x x");
        var tool = new EditFileTool();

        var rejected = await tool.ExecuteAsync(
            Args(new { path = "a.txt", old_string = "x", new_string = "y" }),
            Context(work.Root), CancellationToken.None);
        rejected.Kind.Should().Be(ToolResultKind.InvalidInput);
        rejected.Content.Should().Contain("occurs 3 times");

        var replaced = await tool.ExecuteAsync(
            Args(new { path = "a.txt", old_string = "x", new_string = "y", replace_all = true }),
            Context(work.Root), CancellationToken.None);
        replaced.Kind.Should().Be(ToolResultKind.Success);
        replaced.Content.Should().Contain("3 replacements");
        (await File.ReadAllTextAsync(file)).Should().Be("y y y");
    }

    [Fact]
    public async Task MissingMatchIsInvalidInput()
    {
        using var work = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(work.Root, "a.txt"), "hello");
        var tool = new EditFileTool();

        var result = await tool.ExecuteAsync(
            Args(new { path = "a.txt", old_string = "missing", new_string = "x" }),
            Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
        result.Content.Should().ContainEquivalentOf("not found");
    }

    [Fact]
    public async Task EmptyOldStringIsRejected()
    {
        using var work = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(work.Root, "a.txt"), "hello");
        var tool = new EditFileTool();

        var result = await tool.ExecuteAsync(
            Args(new { path = "a.txt", old_string = "", new_string = "x" }),
            Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
    }

    [Fact]
    public async Task MissingFileIsErrored()
    {
        using var work = new TempWorkspace();
        var tool = new EditFileTool();

        var result = await tool.ExecuteAsync(
            Args(new { path = "absent.txt", old_string = "a", new_string = "b" }),
            Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
        result.Content.Should().ContainEquivalentOf("not found");
    }

    [Fact]
    public async Task IdenticalReplacementIsAnEmptyNoOp()
    {
        using var work = new TempWorkspace();
        var file = Path.Combine(work.Root, "a.txt");
        await File.WriteAllTextAsync(file, "abc");
        var tool = new EditFileTool();

        var result = await tool.ExecuteAsync(
            Args(new { path = "a.txt", old_string = "abc", new_string = "abc" }),
            Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Empty);
        (await File.ReadAllTextAsync(file)).Should().Be("abc"); // unchanged
    }
}
