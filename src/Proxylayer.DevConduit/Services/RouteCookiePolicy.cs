namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Decides what survives of a caller's <c>Cookie</c> header on the way to an upstream.
/// A browser attaches every cookie it holds for the Dev.Conduit origin, and that set includes
/// Dev.Conduit's own session cookie. Forwarding it verbatim would hand each upstream a
/// replayable Dev.Conduit session, so Dev.Conduit's cookies are removed unconditionally and the
/// rest travel only when <c>DevConduit:Route:ForwardCookies</c> says so.
/// </summary>
public static class RouteCookiePolicy
{
    /// <summary>The cookie backing the browser console's sign-in session.</summary>
    public const string SessionCookieName = "DevConduit.Auth";

    /// <summary>
    /// Cookie name prefixes owned by Dev.Conduit's own authentication stack: the antiforgery
    /// token and the OIDC correlation and nonce cookies.
    /// </summary>
    private static readonly string[] OwnedPrefixes =
    [
        ".AspNetCore.Antiforgery.",
        ".AspNetCore.Correlation.",
        ".AspNetCore.OpenIdConnect.Nonce."
    ];

    /// <summary>
    /// The value to forward, or null when nothing should be sent. Dev.Conduit's own cookies are
    /// dropped whatever <paramref name="forwardOthers"/> says.
    /// </summary>
    public static string? Filter(string? cookieHeader, bool forwardOthers)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader) || !forwardOthers)
        {
            return null;
        }

        var kept = cookieHeader
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(pair => !IsOwned(pair))
            .ToArray();

        return kept.Length == 0 ? null : string.Join("; ", kept);
    }

    /// <summary>True when a <c>name=value</c> pair names a cookie Dev.Conduit issued for itself.</summary>
    private static bool IsOwned(string pair)
    {
        var separator = pair.IndexOf('=');
        var name = separator < 0 ? pair : pair[..separator].Trim();

        if (string.Equals(name, SessionCookieName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return OwnedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
