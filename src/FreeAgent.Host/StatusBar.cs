using FreeAgent.Kernel;

namespace FreeAgent.Host;

/// <summary>
/// Opt-in bottom status bar for the console host. Enabled with <c>FREE_STATUS_BAR=1</c>. Uses ANSI
/// cursor-positioning escapes (DECSTBM scroll region + save/restore cursor) so the bottom row
/// stays pinned regardless of how much output scrolls above. When disabled — or when stdout isn't
/// a TTY (e.g. piped output) — every <see cref="Render"/> call is a no-op so the host's behavior
/// is identical to before.
/// </summary>
/// <remarks>
/// This is a stopgap for the proper TUI status bar that lands with the Bun/opentui client (see
/// ADR 0005). The ANSI approach is intentionally minimal — it works on any VT-compatible terminal
/// without a full TUI framework dependency.
/// </remarks>
public sealed class StatusBar : IDisposable
{
    private readonly bool _enabled;
    private readonly int _height;

    public StatusBar()
    {
        _enabled = (Environment.GetEnvironmentVariable("FREE_STATUS_BAR") is "1" or "true")
            && !Console.IsOutputRedirected;
        if (!_enabled) return;

        try { _height = Console.WindowHeight; }
        catch (IOException) { _enabled = false; return; }   // not a real TTY
        if (_height < 4) { _enabled = false; return; }      // too small to be useful

        // Carve out the bottom row as a fixed status area: set the scroll region to lines
        // 1..(height-1), so terminal scrolling never touches the last line.
        Console.Write($"[1;{_height - 1}r");
        // Position the cursor inside the scroll region.
        Console.Write($"[1;1H");
    }

    /// <summary>
    /// Repaint the bottom status row. Pure ANSI: save cursor → move to bottom row → clear it →
    /// write the new content → restore cursor. Caller is responsible for length-bounding the
    /// content if the terminal is narrow; this is intentionally minimal.
    /// </summary>
    public void Render(SessionState state, string model, string providerName)
    {
        if (!_enabled) return;

        var tags = state.Tags.Count == 0 ? "" : $" | tags: {string.Join(",", state.Tags)}";
        var plan = state.PlanMode ? " | PLAN" : "";
        var line = $"FreeAgent | {providerName}/{model} | {state.SessionId} | msgs: {state.Messages.Count} | iter: {state.TotalIterations}{plan}{tags} | cwd: {Truncate(state.WorkingDirectory, 40)}";

        try
        {
            // Save cursor, move to status row, clear it, write, restore cursor.
            Console.Write($"[s[{_height};1H[K[7m{line}[0m[u");
        }
        catch (IOException) { /* terminal disconnected mid-write */ }
    }

    /// <summary>Restore the terminal scroll region so the host's exit doesn't leave a sticky row.</summary>
    public void Dispose()
    {
        if (!_enabled) return;
        try
        {
            Console.Write("[r");          // reset scroll region to full screen
            Console.Write($"[{_height};1H\n"); // park cursor at the bottom
        }
        catch (IOException) { /* terminal gone */ }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : "…" + text[^(max - 1)..];
}
