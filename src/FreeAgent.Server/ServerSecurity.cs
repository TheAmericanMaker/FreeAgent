using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace FreeAgent.Server;

/// <summary>
/// Pure helpers for the protocol server's network-security posture, kept separate from
/// <c>Program</c> so they're unit-testable without an HTTP host. The server is safe-by-default:
/// it binds to loopback unless told otherwise, and it refuses to expose itself on a routable
/// interface without an API key (<see cref="BindsBeyondLoopback"/>). The bearer-token check is
/// constant-time (<see cref="TokenMatches"/>) to avoid a timing oracle on the key.
/// </summary>
public static class ServerSecurity
{
    /// <summary>Default bind — loopback only, so a bare <c>dotnet run</c> is never world-reachable.</summary>
    public const string DefaultBind = "http://127.0.0.1:5000";

    /// <summary>Resolves the bind URL(s): <c>FREEAGENT_SERVER_URLS</c>, then <c>ASPNETCORE_URLS</c>, then the loopback default.</summary>
    public static string ResolveBindUrls(string? freeagentUrls, string? aspnetUrls) =>
        !string.IsNullOrWhiteSpace(freeagentUrls) ? freeagentUrls
        : !string.IsNullOrWhiteSpace(aspnetUrls) ? aspnetUrls
        : DefaultBind;

    /// <summary>
    /// True if any of the (semicolon-separated) bind URLs would listen beyond loopback — a wildcard
    /// (<c>0.0.0.0</c>, <c>+</c>, <c>*</c>, <c>[::]</c>), a specific non-loopback IP, or a DNS host.
    /// Used to refuse an unauthenticated public bind.
    /// </summary>
    public static bool BindsBeyondLoopback(string? urls)
    {
        if (string.IsNullOrWhiteSpace(urls)) return false;

        foreach (var raw in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var host = ExtractHost(raw);
            if (host is "+" or "*" or "0.0.0.0" or "::" or "[::]")
                return true;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                continue;

            var bare = host.Trim('[', ']');
            if (IPAddress.TryParse(bare, out var ip))
            {
                if (IPAddress.IsLoopback(ip)) continue;
                return true; // a specific, non-loopback IP
            }

            // A DNS hostname other than localhost — treat as exposed.
            return true;
        }

        return false;
    }

    /// <summary>
    /// Constant-time check that <paramref name="authorizationHeader"/> carries the required bearer
    /// token. When <paramref name="requiredKey"/> is null/empty the gate is open (returns true) — the
    /// server is unauthenticated, which is only allowed on a loopback bind.
    /// </summary>
    public static bool TokenMatches(string? requiredKey, string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(requiredKey)) return true;
        if (string.IsNullOrEmpty(authorizationHeader)) return false;

        const string prefix = "Bearer ";
        if (!authorizationHeader.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var presented = Encoding.UTF8.GetBytes(authorizationHeader[prefix.Length..]);
        var expected = Encoding.UTF8.GetBytes(requiredKey);
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    private static string ExtractHost(string url)
    {
        var s = url;
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];

        var slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];

        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            return close >= 0 ? s[..(close + 1)] : s;
        }

        var colon = s.IndexOf(':');
        return colon >= 0 ? s[..colon] : s;
    }
}
