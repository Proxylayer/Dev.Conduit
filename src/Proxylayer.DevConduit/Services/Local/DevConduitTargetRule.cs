using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Proxylayer.DevConduit.Services.Local;

/// <summary>
/// One forward-proxy host rule: an entry of a remote's <c>Targets</c> under
/// <c>DevConduit:Remotes</c>, or a route origin. A rule names a host, optionally a port, and
/// optionally a leading wildcard covering subdomains.
/// </summary>
public sealed class DevConduitTargetRule
{
    private readonly string _host;
    private readonly int? _port;
    private readonly bool _wildcard;

    private DevConduitTargetRule(string host, int? port, bool wildcard)
    {
        _host = host;
        _port = port;
        _wildcard = wildcard;
    }

    /// <summary>The rule as it was written, for diagnostics.</summary>
    public override string ToString() =>
        (_wildcard ? "*" : string.Empty) + _host + (_port is { } port ? $":{port}" : string.Empty);

    /// <summary>
    /// Parses a rule. Accepted forms: <c>host</c>, <c>host:port</c>, <c>*.example.com</c>,
    /// <c>10.0.0.5:8080</c>, <c>[::1]:8080</c>, and any of those with a scheme prefix.
    /// </summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out DevConduitTargetRule? rule)
    {
        rule = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();

        // A scheme is tolerated so a rule can be copied straight out of a base address.
        var schemeSeparator = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator >= 0)
        {
            text = text[(schemeSeparator + 3)..];
        }

        var slash = text.IndexOf('/');
        if (slash >= 0)
        {
            text = text[..slash];
        }

        if (text.Length == 0)
        {
            return false;
        }

        var wildcard = false;
        if (text.StartsWith("*.", StringComparison.Ordinal))
        {
            wildcard = true;
            text = text[1..];
        }

        if (!TrySplitPort(text, out var host, out var port) || host.Length == 0)
        {
            return false;
        }

        rule = new DevConduitTargetRule(Normalize(host), port, wildcard);
        return true;
    }

    /// <summary>
    /// Uri.Host reports an IPv6 literal in brackets, so a rule written either way has to
    /// end up in the bracketed form to compare equal.
    /// </summary>
    private static string Normalize(string host) =>
        !host.StartsWith('[')
        && IPAddress.TryParse(host, out var address)
        && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{host}]"
            : host;

    /// <summary>True when the request URI's authority is covered by this rule.</summary>
    public bool Matches(Uri uri)
    {
        if (_port is { } port && uri.Port != port)
        {
            return false;
        }

        var host = uri.Host;

        return _wildcard
            ? host.EndsWith(_host, StringComparison.OrdinalIgnoreCase) && host.Length > _host.Length
            : string.Equals(host, _host, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TrySplitPort(string text, out string host, out int? port)
    {
        host = text;
        port = null;

        if (text.StartsWith('['))
        {
            // Bracketed IPv6 literal, optionally followed by :port.
            var close = text.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            host = text[1..close];
            var tail = text[(close + 1)..];
            if (tail.Length == 0)
            {
                return true;
            }

            return tail[0] == ':' && TryPort(tail[1..], out port);
        }

        var separator = text.LastIndexOf(':');
        if (separator < 0)
        {
            return true;
        }

        // A bare IPv6 literal has several colons and no port.
        if (text.IndexOf(':') != separator)
        {
            return IPAddress.TryParse(text, out _);
        }

        host = text[..separator];
        return TryPort(text[(separator + 1)..], out port);
    }

    private static bool TryPort(string text, out int? port)
    {
        port = null;
        if (!int.TryParse(text, out var parsed) || parsed is < 1 or > 65535)
        {
            return false;
        }

        port = parsed;
        return true;
    }
}
