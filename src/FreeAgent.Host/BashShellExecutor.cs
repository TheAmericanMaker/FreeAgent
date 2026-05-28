using System.Diagnostics;
using FreeAgent.Kernel;

namespace FreeAgent.Host;

/// <summary>
/// Default <see cref="IShellExecutor"/> for the host — runs a command through <c>bash -c</c> with a
/// timeout, streaming the hook's stdout/stderr to the user's console (so hook output is visible but
/// not in the model's conversation). Per-hook timeout defaults to 30s; on timeout the process tree
/// is killed and a non-zero exit code is returned (errors are non-fatal — <see cref="HookRunner"/>
/// swallows them).
/// </summary>
public sealed class BashShellExecutor : IShellExecutor
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _timeout;

    public BashShellExecutor(TimeSpan? timeout = null) => _timeout = timeout ?? DefaultTimeout;

    public async ValueTask<int> RunAsync(string command, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("bash")
        {
            ArgumentList = { "-c", command },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!string.IsNullOrEmpty(stdout)) Console.Out.Write(stdout);
        if (!string.IsNullOrEmpty(stderr)) Console.Error.Write(stderr);

        return process.HasExited ? process.ExitCode : -1;
    }
}
