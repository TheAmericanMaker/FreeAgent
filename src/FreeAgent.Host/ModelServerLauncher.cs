using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
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

    /// <summary>Directory where downloaded GGUFs live; exposed so tests can prepare it.</summary>
    public static string ModelsDir() => Path.Combine(CacheDir(), "models");

    /// <summary>
    /// Translate a model identifier into the path the launcher should hand to <c>llama-server -m</c>.
    /// An existing absolute or relative path passes through unchanged. A bare name (with or without
    /// the <c>.gguf</c> extension) is looked up in <see cref="ModelsDir"/>. If nothing matches the
    /// input is returned untouched so the caller's "file not found" error surfaces.
    /// </summary>
    public static string ResolveModelName(string nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath)) return nameOrPath;
        if (File.Exists(nameOrPath)) return Path.GetFullPath(nameOrPath);

        var direct = Path.Combine(ModelsDir(), nameOrPath);
        if (File.Exists(direct)) return direct;

        if (!nameOrPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            var withExt = Path.Combine(ModelsDir(), nameOrPath + ".gguf");
            if (File.Exists(withExt)) return withExt;
        }
        return nameOrPath;
    }

    /// <summary>
    /// One-line summary of every <c>.gguf</c> file in <see cref="ModelsDir"/>. Sorted by name for
    /// determinism. Returns a friendly empty-state message when the dir is missing or empty.
    /// </summary>
    public static string ListCatalog()
    {
        var dir = ModelsDir();
        if (!Directory.Exists(dir))
            return "No models downloaded yet. Try '/serve download hf:owner/repo/path/to/model.gguf'.";
        var files = Directory.EnumerateFiles(dir, "*.gguf")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
            return "No models downloaded yet. Try '/serve download hf:owner/repo/path/to/model.gguf'.";
        var width = files.Max(f => Path.GetFileName(f).Length);
        var lines = files.Select(f =>
        {
            var name = Path.GetFileName(f).PadRight(width);
            var size = FormatBytes(new FileInfo(f).Length);
            return $"  {name}  {size}";
        });
        return $"Cached models in {dir}:\n" + string.Join('\n', lines);
    }

    /// <summary>
    /// Download a GGUF into <see cref="ModelsDir"/>. <paramref name="source"/> can be a plain HTTPS
    /// URL or a <c>hf:owner/repo/path/to/file.gguf</c> shorthand for HuggingFace's
    /// <c>/resolve/main/</c> URL. Streams the body to a <c>.part</c> file and renames on completion
    /// so an interrupted download leaves no half-file under the model name. <c>HF_TOKEN</c>, if
    /// set, is forwarded as a Bearer token for gated repositories.
    /// </summary>
    public static async Task<string> DownloadAsync(string source, string? overrideName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
            return "Usage: /serve download <url-or-hf-spec> [--name <local-name>]";

        Directory.CreateDirectory(ModelsDir());
        string url;
        try { url = ResolveSourceToUrl(source); }
        catch (ArgumentException ex) { return ex.Message; }

        var fileName = !string.IsNullOrWhiteSpace(overrideName)
            ? overrideName!
            : InferFileName(url);
        if (string.IsNullOrWhiteSpace(fileName))
            return "Couldn't infer a filename from the source — pass --name <local-name>.";
        if (!fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            return $"Refusing to download '{fileName}' — only .gguf files are accepted into the model catalog.";
        if (fileName.Contains('/') || fileName.Contains('\\'))
            return $"--name '{fileName}' must be a bare filename (no separators).";

        var destination = Path.Combine(ModelsDir(), fileName);
        if (File.Exists(destination))
            return $"Already cached: {destination}";
        var tempPath = destination + ".part";

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        if (url.Contains("huggingface.co", StringComparison.OrdinalIgnoreCase)
            && Environment.GetEnvironmentVariable("HF_TOKEN") is { Length: > 0 } token)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var snippet = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return $"Download failed: HTTP {(int)resp.StatusCode} {resp.StatusCode}. {Truncate(snippet, 200)}";
            }

            var total = resp.Content.Headers.ContentLength;
            var buffer = new byte[1 << 16];
            long copied = 0;
            var lastReport = 0L;
            using (var src = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var dst = File.Create(tempPath))
            {
                int n;
                while ((n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    copied += n;
                    if (total is { } expected && expected > 0 && copied - lastReport > expected / 20)
                    {
                        Console.Error.Write($"\r[freeagent] {fileName}: {100.0 * copied / expected:F0}% ({FormatBytes(copied)}/{FormatBytes(expected)})");
                        lastReport = copied;
                    }
                }
            }
            Console.Error.WriteLine();

            File.Move(tempPath, destination);
            return $"Downloaded: {destination} ({FormatBytes(new FileInfo(destination).Length)})";
        }
        catch (OperationCanceledException)
        {
            TryCleanupTemp(tempPath);
            return "Download cancelled.";
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UriFormatException)
        {
            TryCleanupTemp(tempPath);
            return $"Download failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Turns a source spec into a download URL. <c>hf:owner/repo/file/path.gguf</c> maps to
    /// <c>https://huggingface.co/owner/repo/resolve/main/file/path.gguf</c>; anything else is
    /// passed through unchanged (so plain HTTPS URLs Just Work). Exposed for tests.
    /// </summary>
    public static string ResolveSourceToUrl(string source)
    {
        if (source.StartsWith("hf:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = source[3..];
            var firstSlash = rest.IndexOf('/');
            var secondSlash = firstSlash >= 0 ? rest.IndexOf('/', firstSlash + 1) : -1;
            if (firstSlash <= 0 || secondSlash <= firstSlash + 1 || secondSlash >= rest.Length - 1)
                throw new ArgumentException($"Invalid hf: spec '{source}' — expected hf:owner/repo/path/to/file.gguf");
            var owner = rest[..firstSlash];
            var repo = rest[(firstSlash + 1)..secondSlash];
            var filePath = rest[(secondSlash + 1)..];
            return $"https://huggingface.co/{owner}/{repo}/resolve/main/{filePath}";
        }
        return source;
    }

    private static string InferFileName(string url)
    {
        try
        {
            var uri = new Uri(url);
            return Path.GetFileName(uri.LocalPath);
        }
        catch (UriFormatException) { return string.Empty; }
    }

    private static void TryCleanupTemp(string tempPath)
    {
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort */ }
    }

    private static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + "…";

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B"
    };
}
