using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// The four outbound client configurations: redirects on/off crossed with
/// TLS validation on/off. Named clients keep handler lifetimes pooled. Every client
/// that can reach an arbitrary destination refuses to connect into
/// <c>DevConduit:BlockedEgressNetworks</c>, and on a server to anything
/// <c>DevConduit:Route:AllowedTargets</c> does not cover.
/// </summary>
public static class ProxyHttpClients
{
    public const string Default = "proxy";
    public const string FollowRedirects = "proxy-follow";
    public const string InsecureTls = "proxy-insecure";
    public const string FollowRedirectsInsecureTls = "proxy-follow-insecure";

    /// <summary>
    /// The transparent forwarding client. Nothing is rewritten on its behalf: redirects
    /// are relayed as 3xx responses and payloads keep whatever Content-Encoding the
    /// upstream chose, so the caller receives the bytes the upstream actually sent.
    /// </summary>
    public const string Route = "proxy-route";

    /// <summary>
    /// The local-mode relay client. Identical to <see cref="Route"/> except that it ignores
    /// the ambient proxy settings: a developer running local mode has very likely pointed
    /// HTTP_PROXY at this process, and honouring that here would make every relayed call
    /// re-enter the proxy it just left.
    /// </summary>
    public const string LocalRelay = "proxy-local-relay";

    public static string NameFor(bool followRedirects, bool allowInvalidCertificates) =>
        (followRedirects, allowInvalidCertificates) switch
        {
            (false, false) => Default,
            (true, false) => FollowRedirects,
            (false, true) => InsecureTls,
            (true, true) => FollowRedirectsInsecureTls
        };

    public static IServiceCollection AddProxyHttpClients(this IServiceCollection services)
    {
        services.TryAddSingleton(provider => new EgressNetworkGuard(
            provider.GetRequiredService<IOptions<DevConduitOptions>>().Value.BlockedEgressNetworks));
        services.TryAddSingleton(provider => TargetAllowList.For(
            provider.GetRequiredService<IOptions<DevConduitOptions>>().Value,
            provider.GetRequiredService<RouteAliasTable>()));

        AddClient(services, Default, followRedirects: false, allowInvalidCertificates: false);
        AddClient(services, FollowRedirects, followRedirects: true, allowInvalidCertificates: false);
        AddClient(services, InsecureTls, followRedirects: false, allowInvalidCertificates: true);
        AddClient(services, FollowRedirectsInsecureTls, followRedirects: true, allowInvalidCertificates: true);
        AddClient(services, Route, followRedirects: false, allowInvalidCertificates: false, decompress: false);

        // Unguarded: this client only ever talks to the configured remote Dev.Conduit. A remote's
        // certificate is also accepted when it is pinned by a .conduit import.
        services.AddClientTrust();
        AddClient(
            services, LocalRelay, followRedirects: false, allowInvalidCertificates: false,
            decompress: false, useProxy: false, guardEgress: false, acceptPinnedRemotes: true);
        return services;
    }

    /// <summary>
    /// The connect step of every guarded client. Resolving here, rather than trusting the
    /// URL, means the check applies to the address actually dialled: a DNS name that
    /// rebinds to a blocked address, or a redirect that points at one, is refused too.
    /// When an ambient proxy is in use the handler connects to the proxy, so
    /// <paramref name="endPoint"/> is the proxy's and the target is not checked here.
    /// </summary>
    /// <exception cref="EgressBlockedException">The host resolves into a blocked network.</exception>
    public static ValueTask<Stream> ConnectGuardedAsync(
        EgressNetworkGuard guard,
        DnsEndPoint endPoint,
        CancellationToken cancellationToken) =>
        ConnectGuardedAsync(guard, TargetAllowList.Unrestricted, endPoint, cancellationToken);

    /// <summary>
    /// The connect step with the server's target allow-list applied too, after the blocked
    /// networks: to the name dialled, and to every address it resolved to for a network entry.
    /// </summary>
    /// <exception cref="EgressBlockedException">The host resolves into a blocked network.</exception>
    /// <exception cref="TargetNotAllowedException">The allow-list does not cover the host or its addresses.</exception>
    public static async ValueTask<Stream> ConnectGuardedAsync(
        EgressNetworkGuard guard,
        TargetAllowList targets,
        DnsEndPoint endPoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(endPoint);

        var addresses = await ResolveAsync(endPoint.Host, cancellationToken).ConfigureAwait(false);
        guard.EnsureAllowed(endPoint.Host, addresses);
        targets.EnsureConnectionAllowed(endPoint.Host, endPoint.Port, addresses);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, endPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        var literal = host.Length > 1 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;
        if (IPAddress.TryParse(literal, out var address))
        {
            return [address];
        }

        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        return addresses;
    }

    private static void AddClient(
        IServiceCollection services,
        string name,
        bool followRedirects,
        bool allowInvalidCertificates,
        bool decompress = true,
        bool useProxy = true,
        bool guardEgress = true,
        bool acceptPinnedRemotes = false)
    {
        services
            .AddHttpClient(name, client =>
            {
                // Per-request timeouts are enforced with a CancellationTokenSource.
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.Clear();
            })
            .ConfigurePrimaryHttpMessageHandler(provider =>
            {
                var handler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = followRedirects,
                    AutomaticDecompression = decompress ? DecompressionMethods.All : DecompressionMethods.None,
                    UseCookies = false,
                    UseProxy = useProxy,
                    MaxAutomaticRedirections = 10,
                    ConnectTimeout = TimeSpan.FromSeconds(30)
                };

                if (allowInvalidCertificates)
                {
                    handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
                }
                else if (acceptPinnedRemotes)
                {
                    handler.SslOptions.RemoteCertificateValidationCallback = provider.GetRequiredService<RemoteCertificatePolicy>().Validate;
                }

                if (guardEgress)
                {
                    var guard = provider.GetRequiredService<EgressNetworkGuard>();
                    var targets = provider.GetRequiredService<TargetAllowList>();
                    handler.ConnectCallback = (context, cancellationToken) =>
                        ConnectGuardedAsync(guard, targets, context.DnsEndPoint, cancellationToken);
                }

                return handler;
            });
    }
}
