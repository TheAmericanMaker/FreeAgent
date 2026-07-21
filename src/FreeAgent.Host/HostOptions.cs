namespace FreeAgent.Host;

/// <summary>
/// Parsed command-line options for the host. Kept separate from <see cref="Program"/> so the
/// (pure) argument parsing is unit-testable.
/// </summary>
public sealed record HostOptions(
    bool Verbose,
    bool Resume,
    string? ResumeId,
    bool Help,
    bool Version,
    HostSubcommand Subcommand = HostSubcommand.Repl,
    bool Trust = false)
{
    /// <summary>
    /// Recognises a leading subcommand (<c>setup</c>) plus <c>--help</c>/<c>-h</c>,
    /// <c>--version</c>, <c>--verbose</c>/<c>-v</c>, and <c>--resume [id]</c>. After
    /// <c>--resume</c>, a following token that is not itself a flag is taken as the session id.
    /// </summary>
    public static HostOptions Parse(string[] args)
    {
        var verbose = false;
        var resume = false;
        string? resumeId = null;
        var help = false;
        var version = false;
        var trust = false;
        var subcommand = HostSubcommand.Repl;

        // First positional token (no leading dash) is treated as a subcommand if it's recognized.
        // Anything else falls through to flag parsing so legacy invocations still work.
        var startIndex = 0;
        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            switch (args[0].ToLowerInvariant())
            {
                case "setup":
                    subcommand = HostSubcommand.Setup;
                    startIndex = 1;
                    break;
                case "trust":
                    subcommand = HostSubcommand.Trust;
                    startIndex = 1;
                    break;
            }
        }

        for (var i = startIndex; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h":
                    help = true;
                    break;
                case "--version":
                    version = true;
                    break;
                case "--verbose" or "-v":
                    verbose = true;
                    break;
                case "--trust":
                    trust = true;
                    break;
                case "--resume":
                    resume = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                        resumeId = args[++i];
                    break;
            }
        }

        return new HostOptions(verbose, resume, resumeId, help, version, subcommand, trust);
    }
}

/// <summary>Top-level subcommand requested on the command line.</summary>
public enum HostSubcommand
{
    /// <summary>Default — start the interactive REPL.</summary>
    Repl,
    /// <summary>Run the interactive provider-config wizard (<c>freeagent setup</c>).</summary>
    Setup,
    /// <summary>Trust the current directory's executable config (<c>freeagent trust</c>), then exit.</summary>
    Trust,
}
