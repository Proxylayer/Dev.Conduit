namespace Proxylayer.DevConduit.Services.Local;

/// <summary>
/// The plain-text bodies the forward proxy answers with. They are written out here rather
/// than inline because the one a developer is most likely to meet — the refusal to
/// intercept HTTPS — has to teach as well as refuse.
/// </summary>
public static class ForwardProxyMessages
{
    /// <summary>
    /// Answer to a CONNECT for a host that matches a target rule. The request is HTTPS end
    /// to end, so relaying it through Dev.Conduit would mean terminating TLS with a certificate
    /// the client trusts. This tool does not do that, and the reverse-proxy form covers the
    /// same host without needing it.
    /// </summary>
    public static string HttpsInterceptionRefused(string host, int port, int localPort, IReadOnlyCollection<string> aliases)
    {
        var aliasLine = aliases.Count == 0
            ? "  (no routes are configured on this instance; use the header form above)"
            : $"  http://127.0.0.1:{localPort}/route/{aliases.First()}/your/path"
              + $"{Environment.NewLine}  configured routes: {string.Join(", ", aliases)}";

        return $"""
                Dev.Conduit local mode cannot relay this request.

                The client asked for a CONNECT tunnel to {host}:{port}, which means the traffic is
                HTTPS from the client to that host. {host} matches a route under DevConduit:Routes or
                a Targets rule under DevConduit:Remotes, so it is a host you asked to route, but a
                forward proxy sees only encrypted bytes inside a tunnel. Rewriting the request
                would mean terminating TLS here and re-issuing a certificate for {host} from a
                certificate authority your runtime trusts. This tool deliberately does not
                generate, install or trust one.

                Use the reverse-proxy form for this host instead. Send the request to Dev.Conduit
                directly and name the upstream:

                  http://127.0.0.1:{localPort}/route/your/path
                  X-DevConduit-Target: https://{host}:{port}

                or, with a route configured, put its name in the path and send no headers at all:

                {aliasLine}

                Plain http:// requests to {host} through this forward proxy are relayed normally.
                Only CONNECT, which is to say https://, is refused.
                """;
    }

    /// <summary>Answer to a request the proxy could parse but cannot act on.</summary>
    public static string RelativeFormRefused(int localPort) =>
        $"""
         Dev.Conduit local mode received a request with a relative target on its forward-proxy port.

         A forward proxy is addressed with the absolute form, for example:

           GET http://host:port/path HTTP/1.1

         which is what a client sends when HTTP_PROXY points at this listener. A request line
         carrying only a path means the client treated this port as an ordinary web server.

         If you meant to use the reverse proxy, that is a different port:

           http://127.0.0.1:{localPort}/route/your/path
         """;

    /// <summary>Answer to a chunked request body, which the forward proxy does not re-frame.</summary>
    public const string ChunkedNotSupported =
        """
        Dev.Conduit local mode does not relay chunked request bodies through its forward proxy.

        The forward proxy relays length-delimited bodies only. Send the request with a
        Content-Length header, or use the reverse-proxy form on the /route endpoint, which
        streams a chunked body without re-framing it.
        """;
}
