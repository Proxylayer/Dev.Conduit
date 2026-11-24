using System.Net;
using System.Net.Sockets;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Tray;

/// <summary>The addresses the tray opens or copies: the server's console, and local mode's console and forward proxy.</summary>
public static class ConsoleAddresses
{
    public const string DefaultServerUrl = "http://localhost:8091/";

    private const string HttpsUrlKey = "Kestrel:Endpoints:Https:Url";
    private const string HttpsSubjectKey = "Kestrel:Endpoints:Https:Certificate:Subject";

    private static readonly string[] WildcardHosts = ["*", "+", "0.0.0.0", "[::]", "::"];

    /// <summary>
    /// The server's console. The app-managed endpoint, <c>https://&lt;Server:Hostname&gt;:&lt;Server:Port&gt;/</c>,
    /// when a host name is set and no <c>Kestrel:Endpoints</c> URL overrides it, by the rule the
    /// server itself applies (<see cref="ManagedHttpsEndpoint.IsOverridden"/>). Otherwise an
    /// explicit HTTPS endpoint when one is configured: a wildcard host such as <c>0.0.0.0</c>
    /// cannot be browsed to, so the certificate's subject, which the browser will validate
    /// against, stands in for it, or failing that localhost. Else the first configured listen
    /// URL; without one, <see cref="DefaultServerUrl"/>.
    /// </summary>
    public static Uri Server(IConfiguration configuration)
    {
        var https = configuration[HttpsUrlKey];
        var managed = configuration.GetSection(ServerHostOptions.SectionName).Get<ServerHostOptions>() ?? new ServerHostOptions();
        if (!ManagedHttpsEndpoint.IsOverridden(configuration)
            && managed.IsManaged
            && managed.Problem() is null
            && Uri.TryCreate($"https://{managed.NormalizedHostname}:{managed.Port}/", UriKind.Absolute, out var managedUri))
        {
            return managedUri;
        }

        var url = !string.IsNullOrWhiteSpace(https)
            ? https.Trim()
            : ModeBanner.ResolveConfiguredListenUrls(configuration).FirstOrDefault();

        if (string.IsNullOrWhiteSpace(url) || !TrySplit(url, out var scheme, out var host, out var port))
        {
            return new Uri(DefaultServerUrl);
        }

        if (WildcardHosts.Contains(host, StringComparer.Ordinal))
        {
            var subject = configuration[HttpsSubjectKey]?.Trim();
            host = scheme == Uri.UriSchemeHttps && IsHostName(subject) ? subject! : "localhost";
        }

        return Uri.TryCreate($"{scheme}://{host}{(port is null ? string.Empty : ":" + port)}/", UriKind.Absolute, out var uri)
            ? uri
            : new Uri(DefaultServerUrl);
    }

    /// <summary>Local mode's console, on its loopback address.</summary>
    public static Uri LocalConsole(LocalModeOptions local) => new($"http://{LoopbackHost(local)}:{local.Port}/");

    /// <summary>The forward proxy address applications are pointed at.</summary>
    public static string ForwardProxy(LocalModeOptions local) => $"http://{LoopbackHost(local)}:{local.ForwardProxyPort}";

    private static string LoopbackHost(LocalModeOptions local) =>
        local.TryResolveBindAddress(out var address, out _) && address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : (address ?? IPAddress.Loopback).ToString();

    /// <summary>Splits a Kestrel URL, whose host may be a wildcard <see cref="Uri"/> rejects.</summary>
    private static bool TrySplit(string url, out string scheme, out string host, out string? port)
    {
        scheme = host = string.Empty;
        port = null;

        var separator = url.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        scheme = url[..separator].ToLowerInvariant();
        var authority = url[(separator + 3)..].Split('/')[0];

        var portStart = authority.LastIndexOf(':');
        if (portStart > 0 && authority.IndexOf(']', portStart) < 0 && authority[(portStart + 1)..].All(char.IsAsciiDigit))
        {
            port = authority[(portStart + 1)..];
            authority = authority[..portStart];
        }

        host = authority;
        return host.Length > 0 && (scheme == Uri.UriSchemeHttp || scheme == Uri.UriSchemeHttps);
    }

    private static bool IsHostName(string? value) =>
        !string.IsNullOrEmpty(value) && Uri.CheckHostName(value) == UriHostNameType.Dns;
}
