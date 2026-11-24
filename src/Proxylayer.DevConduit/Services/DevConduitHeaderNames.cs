namespace Proxylayer.DevConduit.Services;

/// <summary>The headers the Dev.Conduit forwarding contract is built on.</summary>
public static class DevConduitHeaderNames
{
    /// <summary>Names the upstream origin a forwarded request is destined for.</summary>
    public const string Target = "X-DevConduit-Target";

    /// <summary>Carries credentials meant for the upstream; the server renames it to Authorization.</summary>
    public const string UpstreamAuthorization = "X-DevConduit-Upstream-Authorization";

    /// <summary>Credentials meant for Dev.Conduit itself.</summary>
    public const string Authorization = "Authorization";

    /// <summary>Carries the optional local-mode shared secret.</summary>
    public const string LocalSecret = "X-DevConduit-Local-Secret";

    /// <summary>
    /// Names the remote Dev.Conduit instance a local-mode capture was relayed to. Recorded with
    /// the capture only; never sent.
    /// </summary>
    public const string Remote = "X-DevConduit-Remote";

    public const string Cookie = "Cookie";

    public const string Host = "Host";
}
