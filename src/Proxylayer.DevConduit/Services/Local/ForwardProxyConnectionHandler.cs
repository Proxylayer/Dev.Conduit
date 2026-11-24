using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Services.Local;

/// <summary>
/// Serves one accepted forward-proxy connection: parses each request, decides whether the
/// host is one Dev.Conduit was asked to route, and either relays it through the remote instance
/// that rule names or gets out of the way. Stateless and shared; everything about a connection lives in
/// locals for the duration of <see cref="HandleAsync"/>.
/// </summary>
public sealed class ForwardProxyConnectionHandler
{
    /// <summary>How long a peer may take to finish sending a request head.</summary>
    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long an idle keep-alive connection is held before it is closed.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(120);

    private static readonly byte[] TunnelEstablished =
        "HTTP/1.1 200 Connection Established\r\nConnection: close\r\n\r\n"u8.ToArray();

    private readonly DevConduitTargetMatcher _matcher;
    private readonly RemoteRelay _relay;
    private readonly LocalSharedSecret _secret;
    private readonly RouteAliasTable _aliases;
    private readonly IHttpClientFactory _clients;
    private readonly DevConduitOptions _options;
    private readonly ILogger<ForwardProxyConnectionHandler> _logger;

    public ForwardProxyConnectionHandler(
        DevConduitTargetMatcher matcher,
        RemoteRelay relay,
        LocalSharedSecret secret,
        RouteAliasTable aliases,
        IHttpClientFactory clients,
        IOptions<DevConduitOptions> options,
        ILogger<ForwardProxyConnectionHandler> logger)
    {
        _matcher = matcher;
        _relay = relay;
        _secret = secret;
        _aliases = aliases;
        _clients = clients;
        _options = options.Value;
        _logger = logger;
    }

    public async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        var first = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(first ? HeadTimeout : IdleTimeout);
            first = false;

            ForwardProxyHeadResult result;
            try
            {
                result = await ForwardProxyHeadReader.ReadAsync(stream, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await SafeErrorAsync(stream, 408, "Request Timeout", "The request head did not arrive in time.")
                    .ConfigureAwait(false);
                return;
            }

            if (result.Closed)
            {
                return;
            }

            if (result.Head is not { } head)
            {
                await SafeErrorAsync(stream, 400, "Bad Request", result.Error ?? "The request could not be parsed.")
                    .ConfigureAwait(false);
                return;
            }

            if (!_secret.Accepts(head.First(DevConduitHeaderNames.LocalSecret)))
            {
                await SafeErrorAsync(stream, 401, "Unauthorized", _secret.Requirement).ConfigureAwait(false);
                return;
            }

            if (head.IsConnect)
            {
                await HandleConnectAsync(stream, head, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!await HandleRequestAsync(stream, head, result.Leftover, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>Relays or forwards one absolute-form request. Returns true to keep the connection.</summary>
    private async Task<bool> HandleRequestAsync(
        Stream client,
        ForwardProxyRequestHead head,
        byte[] leftover,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(head.Target, UriKind.Absolute, out var uri) || !uri.IsAbsoluteUri)
        {
            await SafeErrorAsync(client, 400, "Bad Request", ForwardProxyMessages.RelativeFormRefused(_options.Local.Port))
                .ConfigureAwait(false);
            return false;
        }

        if (head.IsChunked)
        {
            await SafeErrorAsync(client, 501, "Not Implemented", ForwardProxyMessages.ChunkedNotSupported)
                .ConfigureAwait(false);
            return false;
        }

        var declared = head.ContentLength ?? 0;
        var body = new ForwardProxyBodyStream(leftover, client, declared);
        var relayed = _matcher.TryMatch(uri, out var remote);
        var destination = relayed ? RemoteRelay.RelayUri(remote!, uri) : uri;

        using var message = BuildMessage(head, destination, body, declared);

        if (relayed)
        {
            await ApplyRelayHeadersAsync(message, head, uri, remote!, cancellationToken).ConfigureAwait(false);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.Route.EffectiveTimeoutSeconds));

        HttpResponseMessage response;
        try
        {
            response = await _clients
                .CreateClient(ProxyHttpClients.LocalRelay)
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "The forward proxy could not reach {Destination}.", destination);
            await SafeErrorAsync(
                    client,
                    502,
                    "Bad Gateway",
                    $"Dev.Conduit could not reach {destination.GetLeftPart(UriPartial.Authority)}: "
                    + HttpFailureClassifier.Describe(exception))
                .ConfigureAwait(false);
            return false;
        }

        using (response)
        {
            // A body left unread, or bytes belonging to a pipelined request, leave the
            // connection in a state this proxy will not guess at.
            var framed = body.Drained && leftover.Length <= declared;

            _logger.LogDebug(
                "Forward proxy {Method} {Uri} -> {Status} ({Path}).",
                head.Method, uri, (int)response.StatusCode, relayed ? $"relayed to {remote!.Name}" : "direct");

            return await ForwardProxyResponseWriter
                .RelayAsync(client, response, !head.IsHead, framed && !head.WantsClose, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tunnels a CONNECT, or refuses one for a host Dev.Conduit was asked to route: inside a
    /// tunnel there is nothing to rewrite without terminating TLS.
    /// </summary>
    private async Task HandleConnectAsync(Stream client, ForwardProxyRequestHead head, CancellationToken cancellationToken)
    {
        if (!TrySplitAuthority(head.Target, out var host, out var port))
        {
            await SafeErrorAsync(client, 400, "Bad Request", $"'{head.Target}' is not a host:port authority.")
                .ConfigureAwait(false);
            return;
        }

        if (Uri.TryCreate($"https://{head.Target}", UriKind.Absolute, out var probe) && _matcher.ShouldRoute(probe))
        {
            _logger.LogInformation(
                "Refusing CONNECT {Host}:{Port}: the host matches a target rule but the traffic is HTTPS.",
                host, port);

            await SafeErrorAsync(
                    client,
                    502,
                    "Bad Gateway",
                    ForwardProxyMessages.HttpsInterceptionRefused(host, port, _options.Local.Port, _aliases.Names))
                .ConfigureAwait(false);
            return;
        }

        using var origin = new TcpClient();
        try
        {
            await origin.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            await SafeErrorAsync(client, 502, "Bad Gateway", $"Could not connect to {host}:{port}: {exception.Message}")
                .ConfigureAwait(false);
            return;
        }

        await client.WriteAsync(TunnelEstablished, cancellationToken).ConfigureAwait(false);
        await client.FlushAsync(cancellationToken).ConfigureAwait(false);

        await TunnelAsync(client, origin, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copies bytes both ways until either side stops. Nothing here is inspected.</summary>
    private static async Task TunnelAsync(Stream client, TcpClient origin, CancellationToken cancellationToken)
    {
        var originStream = origin.GetStream();

        // A pending socket read does not observe a token, so shutdown closes the socket
        // underneath it instead.
        await using var abort = cancellationToken.Register(origin.Close);

        var upstream = Copy(client, originStream);
        var downstream = Copy(originStream, client);
        await Task.WhenAny(upstream, downstream).ConfigureAwait(false);

        static async Task Copy(Stream from, Stream to)
        {
            try
            {
                await from.CopyToAsync(to).ConfigureAwait(false);
                await to.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
            {
                // Either end closing is the normal way a tunnel ends.
            }
        }
    }

    private HttpRequestMessage BuildMessage(ForwardProxyRequestHead head, Uri destination, Stream body, long contentLength)
    {
        var message = new HttpRequestMessage(new HttpMethod(head.Method), destination);

        if (contentLength > 0)
        {
            var content = new StreamContent(body);
            content.Headers.ContentLength = contentLength;
            message.Content = content;
        }

        var connectionTokens = RouteHeaderPolicy.ConnectionTokens(
            new StringValues([head.First("Connection"), head.First("Proxy-Connection")]));

        foreach (var header in head.Headers)
        {
            if (IsConsumed(header.Name) || connectionTokens.Contains(header.Name))
            {
                continue;
            }

            RequestHeaderWriter.Apply(message, header.Name, header.Value, _logger);
        }

        // Host follows the destination, which HttpClient derives from the request URI.
        message.Headers.Host = null;
        return message;
    }

    private async Task ApplyRelayHeadersAsync(
        HttpRequestMessage message,
        ForwardProxyRequestHead head,
        Uri origin,
        RemoteDefinition remote,
        CancellationToken cancellationToken)
    {
        var credentials = await _relay
            .PrepareAsync(
                remote,
                origin,
                head.First(DevConduitHeaderNames.Authorization),
                head.First(DevConduitHeaderNames.UpstreamAuthorization),
                cancellationToken)
            .ConfigureAwait(false);

        message.Headers.TryAddWithoutValidation(DevConduitHeaderNames.Target, credentials.Target);

        if (credentials.UpstreamAuthorization is { } upstream)
        {
            message.Headers.TryAddWithoutValidation(DevConduitHeaderNames.UpstreamAuthorization, upstream);
        }

        message.Headers.TryAddWithoutValidation(DevConduitHeaderNames.Authorization, credentials.Authorization);
    }

    /// <summary>Headers this proxy consumes, re-issues itself, or must not relay.</summary>
    private static bool IsConsumed(string name) =>
        RouteHeaderPolicy.HopByHop.Contains(name)
        || string.Equals(name, "Proxy-Connection", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, DevConduitHeaderNames.Host, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, DevConduitHeaderNames.LocalSecret, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, DevConduitHeaderNames.Authorization, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, DevConduitHeaderNames.Target, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, DevConduitHeaderNames.UpstreamAuthorization, StringComparison.OrdinalIgnoreCase);

    private static bool TrySplitAuthority(string authority, out string host, out int port)
    {
        host = authority;
        port = 443;

        var separator = authority.LastIndexOf(':');
        var close = authority.LastIndexOf(']');

        if (separator < 0 || separator < close)
        {
            host = authority.Trim('[', ']');
            return host.Length > 0;
        }

        if (!int.TryParse(authority[(separator + 1)..], out port) || port is < 1 or > 65535)
        {
            return false;
        }

        host = authority[..separator].Trim('[', ']');
        return host.Length > 0;
    }

    /// <summary>
    /// Writes an error response, swallowing a write failure. A peer that has already gone
    /// away must not turn into a logged exception on the accept loop.
    /// </summary>
    private async Task SafeErrorAsync(Stream client, int statusCode, string reason, string body)
    {
        try
        {
            await ForwardProxyResponseWriter
                .WriteErrorAsync(client, statusCode, reason, body + Environment.NewLine, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
        {
            _logger.LogDebug(exception, "The forward proxy could not deliver a {Status} response.", statusCode);
        }
    }
}
