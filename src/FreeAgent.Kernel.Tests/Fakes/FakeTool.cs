using System.Text.Json;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests;

public sealed class FakeTool : ITool
{
    private readonly Func<JsonDocument, ToolResult> _execute;

    public FakeTool(string name, Func<JsonDocument, ToolResult> execute, bool isReadOnly = false, bool isConcurrencySafe = false)
    {
        Name = name;
        _execute = execute;
        IsReadOnly = isReadOnly;
        IsConcurrencySafe = isConcurrencySafe;
        InputSchema = JsonDocument.Parse("{}");
    }

    public string Name { get; }
    public JsonDocument InputSchema { get; }
    public bool IsReadOnly { get; }
    public bool IsConcurrencySafe { get; }
    public int ExecutionCount { get; private set; }

    public ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken)
    {
        ExecutionCount++;
        return ValueTask.FromResult(_execute(arguments));
    }
}
