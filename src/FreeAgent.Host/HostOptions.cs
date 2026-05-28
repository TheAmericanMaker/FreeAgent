namespace FreeAgent.Host;

/// <summary>
/// Parsed command-line options for the host. Kept separate from <see cref="Program"/> so the
/// (pure) argument parsing is unit-testable.
/// </summary>
public sealed record HostOptions(bool Verbose, bool Resume, string? ResumeId, bool Help, bool Version)
{
    /// <summary>
    /// Recognises <c>--help</c>/<c>-h</c>, <c>--version</c>, <c>--verbose</c>/<c>-v</c>, and
    /// <c>--resume [id]</c>. After <c>--resume</c>, a following token that is not itself a flag is
    /// taken as the session id to resume.
    /// </summary>
    public static HostOptions Parse(string[] args)
    {
        var verbose = false;
        var resume = false;
        string? resumeId = null;
        var help = false;
        var version = false;

        for (var i = 0; i < args.Length; i++)
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
                case "--resume":
                    resume = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                        resumeId = args[++i];
                    break;
            }
        }

        return new HostOptions(verbose, resume, resumeId, help, version);
    }
}
