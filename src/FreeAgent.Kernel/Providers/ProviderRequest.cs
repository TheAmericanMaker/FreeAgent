namespace FreeAgent.Kernel;

public sealed record ProviderRequest(IReadOnlyList<Message> Messages, IReadOnlyList<ToolDefinition> Tools);
