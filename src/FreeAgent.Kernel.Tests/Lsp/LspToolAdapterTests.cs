using System.Text.Json;
using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Lsp;

public sealed class LspToolAdapterTests
{
    private static JsonDocument Args(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value));

    private static ToolContext Context(string workingDirectory) =>
        new(new SessionState("lsp-session", workingDirectory, DateTimeOffset.UnixEpoch));

    private static LspServerSpec Spec(string name = "csharp", params string[] extensions) =>
        new(name, name, extensions, "fake-lsp", []);

    private static LspToolAdapter NewAdapter(LspToolAdapter.LspAction action, params string[] extensions)
    {
        // We don't actually start the client; the tests below either don't call ExecuteAsync or
        // fail early on path-validation before reaching the wire.
        var client = new LspClient(new NeverReadsTransport());
        return new LspToolAdapter(client, Spec("csharp", extensions), action);
    }

    private sealed class NeverReadsTransport : ILspTransport
    {
        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => new((string?)null);
        public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public void Dispose() { }
    }

    [Fact]
    public void NameAndDescriptionIncludeServerNameAndAction()
    {
        var tool = NewAdapter(LspToolAdapter.LspAction.Hover, ".cs");
        tool.Name.Should().Be("lsp__csharp__hover");
        tool.Description.Should().Contain("Hover").And.Contain("csharp");
    }

    [Fact]
    public void IsReadOnlyButNotConcurrencySafe()
    {
        var tool = NewAdapter(LspToolAdapter.LspAction.Hover);
        tool.IsReadOnly.Should().BeTrue();
        // Server state is shared across calls — the adapter is not concurrency-safe.
        tool.IsConcurrencySafe.Should().BeFalse();
    }

    [Fact]
    public void RequiredCapabilityIsProcessExecScopedToTheServer()
    {
        var tool = NewAdapter(LspToolAdapter.LspAction.Hover);
        var caps = tool.RequiredCapabilities(Args(new { path = "a.cs", line = 1, character = 1 }), Context("/tmp"));
        caps.Should().ContainSingle().Which.Should().BeOfType<ProcessExecCap>()
            .Which.Binary.Should().Be("lsp:csharp");
    }

    [Fact]
    public async Task PathExtensionMismatchIsInvalidInput()
    {
        var tool = NewAdapter(LspToolAdapter.LspAction.Hover, ".cs");
        var result = await tool.ExecuteAsync(
            Args(new { path = "notes.txt", line = 1, character = 1 }),
            Context("/tmp"),
            CancellationToken.None);
        result.Kind.Should().Be(ToolResultKind.InvalidInput);
        result.Content.Should().Contain(".cs");
    }

    [Fact]
    public void InputSchemaForOpenActionTakesOnlyPath()
    {
        var open = NewAdapter(LspToolAdapter.LspAction.Open);
        var schema = open.InputSchema.RootElement;
        schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Should().Equal("path");
    }

    [Fact]
    public void InputSchemaForPositionalActionsTakesPathLineCharacter()
    {
        var hover = NewAdapter(LspToolAdapter.LspAction.Hover);
        var schema = hover.InputSchema.RootElement;
        var props = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        props.Should().Contain(["path", "line", "character"]);
    }
}
