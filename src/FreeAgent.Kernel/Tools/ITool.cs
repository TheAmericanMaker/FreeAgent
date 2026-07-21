using System.Text.Json;

namespace FreeAgent.Kernel;

public interface ITool
{
    string Name { get; }

    /// <summary>
    /// Model-facing description of what the tool does and when to use it. Sent to the provider as
    /// the function description, so it directly shapes tool-selection quality.
    /// </summary>
    string Description { get; }

    JsonDocument InputSchema { get; }
    bool IsReadOnly { get; }
    bool IsConcurrencySafe { get; }

    /// <summary>
    /// The capabilities this call requires, derived from its arguments. An empty list means the
    /// tool needs no special authorization. The pipeline passes the result to the permission engine.
    /// </summary>
    IReadOnlyList<Capability> RequiredCapabilities(JsonDocument arguments, ToolContext context);

    ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken);
}
