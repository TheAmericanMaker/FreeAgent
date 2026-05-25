using System.Text.Json;

namespace FreeAgent.Kernel;

public interface ITool
{
    string Name { get; }
    JsonDocument InputSchema { get; }
    bool IsReadOnly { get; }
    bool IsConcurrencySafe { get; }
    ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken);
}
