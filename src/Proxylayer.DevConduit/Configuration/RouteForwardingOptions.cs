namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// Settings bound from "DevConduit:Route" that govern the transparent forwarding endpoint. The
/// named routes themselves live under "DevConduit:Routes" and are read by
/// <see cref="RoutingConfigurationParser"/>.
/// </summary>
public sealed class RouteForwardingOptions
{
    public const int DefaultTimeoutSeconds = 100;
    public const int MaxTimeoutSeconds = 600;

    /// <summary>When true only configured routes may be forwarded to; X-DevConduit-Target is refused.</summary>
    public bool AliasesOnly { get; set; }

    /// <summary>
    /// Whether the caller's <c>Cookie</c> header reaches the upstream at all. Off by default:
    /// a browser attaches every cookie it holds for the Dev.Conduit origin, and an upstream has
    /// no business receiving them. Dev.Conduit's own session and antiforgery cookies are stripped
    /// whatever this is set to.
    /// </summary>
    public bool ForwardCookies { get; set; }

    /// <summary>
    /// Server mode: the hosts outbound calls may reach, each a host name, a <c>*.</c> wildcard,
    /// or an IP address or CIDR network, optionally with <c>:port</c>. Empty allows every host
    /// not in <c>DevConduit:BlockedEgressNetworks</c>; every configured route's target is
    /// allowed besides. Read by <c>TargetAllowList</c>.
    /// </summary>
    public string[] AllowedTargets { get; set; } = [];

    /// <summary>Per-request upstream timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

    /// <summary>Timeout clamped into a sane range.</summary>
    public int EffectiveTimeoutSeconds =>
        TimeoutSeconds <= 0 ? DefaultTimeoutSeconds : Math.Min(TimeoutSeconds, MaxTimeoutSeconds);
}
