using System.Text.Json;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests;

public sealed class FakeTool : ITool
{
    private readonly Func<JsonDocument, ToolContext, CancellationToken, ValueTask<ToolResult>> _execute;
    private readonly Func<JsonDocument, ToolContext, IReadOnlyList<Capability>> _capabilities;

    public FakeTool(
        string name,
        Func<JsonDocument, ToolResult> execute,
        bool isReadOnly = false,
        bool isConcurrencySafe = false,
        Func<JsonDocument, ToolContext, IReadOnlyList<Capability>>? capabilities = null,
        string schemaJson = "{}",
        string description = "")
        : this(
            name,
            (arguments, _, _) => ValueTask.FromResult(execute(arguments)),
            isReadOnly,
            isConcurrencySafe,
            capabilities,
            schemaJson,
            description)
    {
    }

    public FakeTool(
        string name,
        Func<JsonDocument, ToolContext, CancellationToken, ValueTask<ToolResult>> execute,
        bool isReadOnly = false,
        bool isConcurrencySafe = false,
        Func<JsonDocument, ToolContext, IReadOnlyList<Capability>>? capabilities = null,
        string schemaJson = "{}",
        string description = "")
    {
        Name = name;
        Description = description;
        _execute = execute;
        IsReadOnly = isReadOnly;
        IsConcurrencySafe = isConcurrencySafe;
        InputSchema = JsonDocument.Parse(schemaJson);
        _capabilities = capabilities ?? ((_, _) => []);
    }

    public string Name { get; }
    public string Description { get; }
    public JsonDocument InputSchema { get; }
    public bool IsReadOnly { get; }
    public bool IsConcurrencySafe { get; }
    public int ExecutionCount { get; private set; }

    public IReadOnlyList<Capability> RequiredCapabilities(JsonDocument arguments, ToolContext context) =>
        _capabilities(arguments, context);

    public ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken)
    {
        ExecutionCount++;
        return _execute(arguments, context, cancellationToken);
    }
}
