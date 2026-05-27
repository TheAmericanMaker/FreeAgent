using System.Text.Json;

namespace FreeAgent.Kernel;

public sealed record ToolDefinition(string Name, string Description, JsonDocument InputSchema, bool IsReadOnly, bool IsConcurrencySafe);
