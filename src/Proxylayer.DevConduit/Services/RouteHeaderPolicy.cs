using Microsoft.Extensions.Primitives;
using Proxylayer.DevConduit.Endpoints;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Which headers cross the proxy boundary and in what shape. Hop-by-hop headers describe
/// one TCP connection and must not be relayed; Dev.Conduit's own control headers are for
/// Dev.Conduit alone; the caller's upstream credential is renamed on the way out.
/// </summary>
public static class RouteHeaderPolicy
{
    /// <summary>The header carrying credentials meant for the upstream, not for Dev.Conduit.</summary>
    public const string UpstreamAuthorizationHeader = DevConduitHeaderNames.UpstreamAuthorization;

    public const string AuthorizationHeader = DevConduitHeaderNames.Authorization;

    public static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade"
    };

    /// <summary>Headers Dev.Conduit consumes itself and never relays.</summary>
    public static readonly HashSet<string> Control = new(StringComparer.OrdinalIgnoreCase)
    {
        RouteTargetResolver.TargetHeader,
        ClientHeaderFilter.HeaderName,
        UpstreamAuthorizationHeader,
        DevConduitHeaderNames.LocalSecret
    };

    /// <summary>
    /// The header names listed in a <c>Connection</c> header, which RFC 9110 says are
    /// themselves connection-scoped and must be dropped along with it.
    /// </summary>
    public static HashSet<string> ConnectionTokens(StringValues connection)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in connection)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }
}
