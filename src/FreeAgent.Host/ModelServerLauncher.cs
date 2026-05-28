using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;

namespace FreeAgent.Host;

/// <summary>
/// Lifecycle helper for a local OpenAI-compatible inference server (llama.cpp's
/// <c>llama-server</c> by default; any binary that speaks the same wire protocol works — Ollama,
/// LM Studio, vLLM, exo, etc.). FreeAgent doesn't embed a model runtime — every local engine
/// already speaks OpenAI-compatible HTTP, so the kernel just spawns the server, waits for
/// <c>/health</c>, and lets the user point <c>OPENAI_BASE_URL</c> at it. State (the running PID
/// and a tail log) lives in <c>$XDG_CACHE_HOME/freeagent/</c> so a fresh REPL invocation can
/// detect and reuse an already-running server instead of stomping it.
/// </summary>
/// <remarks>
/// The launcher is intentionally Linux-shaped (PID file, no service registration) — Windows
/// support can land later. Process-detached lifetime is the unix default: spawned child outlives
/// the parent unless explicitly killed, which is exactly what we want for a long-running
/// inference server.
/// </remarks>
public static class ModelServerLauncher
{
    /// <summary>
    /// Spawns the server and polls <c>/health</c>. Returns a status line for the REPL. Idempotent:
    /// if a server is already running per the pid file, returns its details rather than starting
    /// a second one.
    /// </summary>
    public static async Task<string> StartAsync(
        string modelPath,
        int port,
        string binPath,
        string extraArgs,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(CacheDir());

        if (TryReadPid() is { } existing && IsAlive(existing))
            return $"Already running (pid {existing}). Use '/serve status' for details or '/serve stop' to halt it first.";
        if (File.Exists(PidFile())) File.Delete(PidFile()); // stale

        if (!File.Exists(modelPath))
            return $"Model file not found: {modelPath}";
        if (IsPortInUse(port))
            return $"Port {port} is already in use. Choose another with --port.";

        var args = $"-m \"{modelPath}\" --host 127.0.0.1 --port {port}"
            + (string.IsNullOrWhiteSpace(extraArgs) ? "" : " " + extraArgs);

        var psi = new ProcessStartInfo(binPath, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        Process proc;
        try { proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null."); }
        catch (Exception ex)
        {
            return $"Failed to start '{binPath}': {ex.Message}. Install llama-server or pass --bin <path>.";
        }

        File.WriteAllText(PidFile(), proc.Id.ToString());

        // Drain stdout/stderr into the log so the buffers don't fill up and block the server.
        _ = Task.Run(async () =>
        {
            try
            {
                await using var log = new FileStream(LogPath(), FileMode.Append, FileAccess.Write, FileShare.Read);
                await using var writer = new StreamWriter(log);
                var pipeOut = PumpAsync(proc.StandardOutput, writer, "stdout");
                var pipeErr = PumpAsync(proc.StandardError, writer, "stderr");
                await Task.WhenAll(pipeOut, pipeErr);
            }
            catch { /* best-effort logging */ }
        }, CancellationToken.None);

        // Health probe (default endpoint for llama-server, OAI-compat servers usually expose it too).
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var url = $"http://127.0.0.1:{port}/health";
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (proc.HasExited)
                return $"Process exited (code {proc.ExitCode}). See log: {LogPath()}";
            try
            {
                var resp = await http.GetAsync(url, cancellationToken);
                if (resp.IsSuccessStatusCode)
                    return $"Started (pid {proc.Id}, port {port}). Point FreeAgent at it:\n"
                         + $"  OPENAI_BASE_URL=http://127.0.0.1:{port}/v1 FREEMODEL={Path.GetFileNameWithoutExtension(modelPath)} freeagent";
            }
            catch { /* not up yet */ }
            try { await Task.Delay(500, cancellationToken); }
            catch (OperationCanceledException) { return $"Cancelled. Server still running as pid {proc.Id}."; }
        }
        return $"Started (pid {proc.Id}) but /health didn't respond within 30s. Tail the log: {LogPath()}";
    }

    /// <summary>Kills the recorded server process and clears the pid file. Idempotent.</summary>
    public static string Stop()
    {
        var pid = TryReadPid();
        if (pid is null)
            return "Not running (no pid file).";

        if (!IsAlive(pid.Value))
        {
            File.Delete(PidFile());
            return $"Pid file pointed at {pid} but it is not running. Cleaned up.";
        }

        try
        {
            using var proc = Process.GetProcessById(pid.Value);
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Already gone between IsAlive check and Kill.
        }

        File.Delete(PidFile());
        return $"Stopped (pid {pid}).";
    }

    /// <summary>One-line snapshot for <c>/serve status</c>.</summary>
    public static string Status()
    {
        var pid = TryReadPid();
        if (pid is null) return "Not running.";
        return IsAlive(pid.Value)
            ? $"Running (pid {pid}). Log: {LogPath()}"
            : $"Pid file present ({pid}) but process is gone. Run '/serve stop' to clean up.";
    }

    private static async Task PumpAsync(StreamReader reader, StreamWriter writer, string tag)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                await writer.WriteLineAsync($"[{tag}] {line}");
        }
        catch { /* swallow */ }
    }

    private static bool IsAlive(int pid)
    {
        try { using var _ = Process.GetProcessById(pid); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            using var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    private static int? TryReadPid()
    {
        var path = PidFile();
        if (!File.Exists(path)) return null;
        return int.TryParse(File.ReadAllText(path).Trim(), out var pid) && pid > 0 ? pid : null;
    }

    private static string CacheDir()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (string.IsNullOrWhiteSpace(xdg))
            xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return Path.Combine(xdg, "freeagent");
    }

    /// <summary>Path to the pid file. Exposed for tests / external introspection.</summary>
    public static string PidFile() => Path.Combine(CacheDir(), "model-server.pid");

    /// <summary>Path to the rolling log. Exposed for tests / external introspection.</summary>
    public static string LogPath() => Path.Combine(CacheDir(), "model-server.log");
}
