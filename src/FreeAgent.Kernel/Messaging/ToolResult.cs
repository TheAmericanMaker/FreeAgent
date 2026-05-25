namespace FreeAgent.Kernel;

public enum ToolResultKind
{
    Success,
    Error,
    Crash,
    Cancelled
}

public sealed record ToolResult(ToolResultKind Kind, string Content)
{
    public bool IsError => Kind != ToolResultKind.Success;
    public static ToolResult Success(string content) => new(ToolResultKind.Success, content);
    public static ToolResult Error(string content) => new(ToolResultKind.Error, content);
    public static ToolResult Cancelled(string content) => new(ToolResultKind.Cancelled, content);
    public static ToolResult Crash(string content) => new(ToolResultKind.Crash, content);
}
