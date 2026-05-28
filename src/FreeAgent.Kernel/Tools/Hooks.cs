using System.Text.Json.Serialization;

namespace FreeAgent.Kernel;

/// <summary>Optional match condition on a hook.</summary>
public sealed record HookCondition(
    [property: JsonPropertyName("tool")] string? Tool = null,
    [property: JsonPropertyName("inputContains")] string? InputContains = null);

/// <summary>A single hook: a shell command to run, optionally conditioned on the tool call.</summary>
public sealed record HookSpec(
    [property: JsonPropertyName("run")] string Run,
    [property: JsonPropertyName("if")] HookCondition? If = null);

/// <summary>Hooks registered for the pipeline's <c>pre-hook</c> / <c>post-hook</c> seams.</summary>
public sealed record HooksConfig(
    [property: JsonPropertyName("preToolUse")] IReadOnlyList<HookSpec>? PreToolUse = null,
    [property: JsonPropertyName("postToolUse")] IReadOnlyList<HookSpec>? PostToolUse = null);

/// <summary>Runs configured hooks at the pipeline seams. Errors are non-fatal — the agent never blocks on a hook.</summary>
public interface IHookRunner
{
    ValueTask RunPreToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken);

    ValueTask RunPostToolAsync(string toolName, string argumentsJson, ToolResult result, CancellationToken cancellationToken);
}

/// <summary>Executes a shell command. Host-side seam so the kernel doesn't hardcode bash.</summary>
public interface IShellExecutor
{
    /// <summary>Runs <paramref name="command"/>. Returns its exit code (0 = success).</summary>
    ValueTask<int> RunAsync(string command, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IHookRunner"/> — matches each hook against the current tool call and runs the
/// matching ones through an injected <see cref="IShellExecutor"/>. <c>{{tool_name}}</c> and
/// <c>{{tool_input}}</c> are substituted in the shell command before execution. Exceptions and
/// non-zero exit codes are swallowed so a misbehaving hook never blocks the agent.
/// </summary>
public sealed class HookRunner : IHookRunner
{
    private const int MaxInputSubstitutionLength = 2000;

    private readonly IReadOnlyList<HookSpec> _preTool;
    private readonly IReadOnlyList<HookSpec> _postTool;
    private readonly IShellExecutor _shell;

    public HookRunner(HooksConfig? config, IShellExecutor shell)
    {
        _preTool = config?.PreToolUse ?? [];
        _postTool = config?.PostToolUse ?? [];
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
    }

    public ValueTask RunPreToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken) =>
        RunMatchingAsync(_preTool, toolName, argumentsJson, cancellationToken);

    public ValueTask RunPostToolAsync(string toolName, string argumentsJson, ToolResult result, CancellationToken cancellationToken) =>
        RunMatchingAsync(_postTool, toolName, argumentsJson, cancellationToken);

    /// <summary>True if <paramref name="condition"/> (or none) matches the tool call. Pure.</summary>
    public static bool Matches(HookCondition? condition, string toolName, string argumentsJson)
    {
        if (condition is null) return true;
        if (!string.IsNullOrEmpty(condition.Tool)
            && !string.Equals(condition.Tool, toolName, StringComparison.Ordinal))
            return false;
        if (!string.IsNullOrEmpty(condition.InputContains)
            && !argumentsJson.Contains(condition.InputContains, StringComparison.Ordinal))
            return false;
        return true;
    }

    /// <summary>Substitutes <c>{{tool_name}}</c> and <c>{{tool_input}}</c> in the command. Pure.</summary>
    public static string Substitute(string command, string toolName, string argumentsJson)
    {
        var truncated = argumentsJson.Length <= MaxInputSubstitutionLength
            ? argumentsJson
            : argumentsJson[..MaxInputSubstitutionLength] + "…";
        return command
            .Replace("{{tool_name}}", toolName, StringComparison.Ordinal)
            .Replace("{{tool_input}}", truncated, StringComparison.Ordinal);
    }

    private async ValueTask RunMatchingAsync(IReadOnlyList<HookSpec> hooks, string toolName, string argumentsJson, CancellationToken cancellationToken)
    {
        if (hooks.Count == 0) return;
        foreach (var hook in hooks)
        {
            if (cancellationToken.IsCancellationRequested) return;
            if (!Matches(hook.If, toolName, argumentsJson)) continue;

            var command = Substitute(hook.Run, toolName, argumentsJson);
            try
            {
                await _shell.RunAsync(command, cancellationToken);
            }
            catch
            {
                // Hooks are non-fatal — never let one break the agent loop.
            }
        }
    }
}
