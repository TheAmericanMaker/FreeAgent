using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace FreeAgent.Kernel;

/// <summary>
/// Runs an external process in the session working directory and returns its exit code, stdout, and
/// stderr. Never read-only or concurrency-safe — the kernel cannot know what an arbitrary command
/// does. The required capability is a <see cref="ProcessExecCap"/> for the binary and its args; the
/// permission engine auto-allows a small set of safe read-only binaries and hard-blocks others (e.g.
/// sudo), so the tool need not police the binary itself. A timed-out process is killed (with its
/// children); caller cancellation is propagated so the pipeline can map it to a Cancelled result.
/// </summary>
public sealed class ProcessExecTool : ITool
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _timeout;

    public ProcessExecTool(TimeSpan? timeout = null) => _timeout = timeout ?? DefaultTimeout;

    public string Name => "ProcessExec";
    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public JsonDocument InputSchema { get; } = JsonDocument.Parse(
        """{"type":"object","required":["command"],"properties":{"command":{"type":"string"},"args":{"type":"array"}}}""");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonDocument arguments, ToolContext context)
    {
        var (binary, args) = ParseCommand(arguments);
        return [new ProcessExecCap(binary, args)];
    }

    public async ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (binary, args) = ParseCommand(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = binary,
            WorkingDirectory = context.Session.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ToolResult.Error($"Could not start process '{binary}': {ex.Message}");
        }

        // Drain both pipes concurrently to avoid a full-buffer deadlock. No token is passed: the
        // reads complete when the process exits (or is killed) and the streams reach EOF, which
        // keeps the tasks observed even on the cancellation/timeout paths below.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        var timedOut = false;
        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
            }
            else
            {
                timedOut = true;
            }
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (cancelled)
        {
            // Caller cancellation: surface it so the pipeline records a Cancelled result.
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (timedOut)
        {
            return ToolResult.Error(Format($"timed out after {_timeout.TotalSeconds:0.##}s and was killed", stdout, stderr));
        }

        return ToolResult.Success(Format($"Exit code: {process.ExitCode}", stdout, stderr));
    }

    private static string Format(string header, string stdout, string stderr)
    {
        var builder = new StringBuilder(header);
        AppendSection(builder, "stdout", stdout);
        AppendSection(builder, "stderr", stderr);
        return builder.ToString();
    }

    private static void AppendSection(StringBuilder builder, string label, string content)
    {
        var trimmed = content.TrimEnd('\r', '\n');
        if (trimmed.Length == 0)
        {
            return;
        }

        builder.Append("\n--- ").Append(label).Append(" ---\n").Append(trimmed);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already exited or not killable; nothing useful to do.
        }
    }

    private static (string Binary, IReadOnlyList<string> Args) ParseCommand(JsonDocument arguments)
    {
        var root = arguments.RootElement;
        var command = root.GetProperty("command").GetString() ?? string.Empty;

        var args = new List<string>();
        if (root.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in argsElement.EnumerateArray())
            {
                args.Add(item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText());
            }
        }

        // Robustness: the model sometimes packs the whole command line into `command`. Only when no
        // explicit args were given do we split on whitespace — the first token is the binary, the
        // rest are its args. This also normalizes surrounding whitespace on a single-token command.
        if (args.Count == 0)
        {
            var parts = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
            {
                return (parts[0], parts[1..]);
            }

            if (parts.Length == 1)
            {
                return (parts[0], args);
            }
        }

        return (command, args);
    }
}
