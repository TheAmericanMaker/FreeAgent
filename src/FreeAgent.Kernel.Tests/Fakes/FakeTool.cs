using System.Text.Json;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests;

public sealed class FakeTool : ITool
{
    private readonly Func<JsonDocument, ToolResult> _execute;
    private readonly Func<JsonDocument, ToolContext, IReadOnlyList<Capability>> _capabilities;

    public FakeTool(
        string name,
        Func<JsonDocument, ToolResult> execute,
        bool isReadOnly = false,
        bool isConcurrencySafe = false,
        Func<JsonDocument, ToolContext, IReadOnlyList<Capability>>? capabilities = null,
        string schemaJson = "{}")
    {
        Name = name;
        _execute = execute;
        IsReadOnly = isReadOnly;
        IsConcurrencySafe = isConcurrencySafe;
        InputSchema = JsonDocument.Parse(schemaJson);
        _capabilities = capabilities ?? ((_, _) => []);
    }

    public string Name { get; }
    public JsonDocument InputSchema { get; }
    public bool IsReadOnly { get; }
    public bool IsConcurrencySafe { get; }
    public int ExecutionCount { get; private set; }

    public IReadOnlyList<Capability> RequiredCapabilities(JsonDocument arguments, ToolContext context) =>
        _capabilities(arguments, context);

    public ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken)
    {
        ExecutionCount++;
        return ValueTask.FromResult(_execute(arguments));
    }
}
