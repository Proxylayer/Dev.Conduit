using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using Proxylayer.DevConduit.Certificates;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// One entry of <c>DevConduit:Route:AllowedTargets</c>: an exact host name
/// (<c>api.internal.example</c>), a wildcard covering every name below a domain
/// (<c>*.internal.example</c>, not the domain itself), or an IP address or CIDR network
/// (<c>10.0.0.0/16</c>, <c>fd00::/8</c>), each optionally limited to one port. An IPv6 address
/// or network with a port is written in brackets: <c>[fd00::1]:8443</c>, <c>[fd00::/8]:443</c>.
/// Host names match the name a caller asks for; addresses and networks match the address a
/// connection is actually made to. Immutable.
/// </summary>
public sealed class AllowedTarget
{
    private readonly string? _host;
    private readonly bool _wildcard;
    private readonly IPNetwork? _network;

    private AllowedTarget(string? host, bool wildcard, IPNetwork? network, int? port)
    {
        _host = host;
        _wildcard = wildcard;
        _network = network;
        Port = port;
    }

    /// <summary>The one port the entry allows, or null for any port.</summary>
    public int? Port { get; }

    /// <summary>True for an address or network entry, which is checked against the resolved address.</summary>
    public bool IsNetwork => _network is not null;

    /// <summary>The entry in its normalized form, for the startup log.</summary>
    public override string ToString()
    {
        var target = _network is { } network
            ? network.BaseAddress.AddressFamily == AddressFamily.InterNetworkV6 && Port is not null ? $"[{network}]" : network.ToString()
            : (_wildcard ? "*." : string.Empty) + _host;
        return Port is { } port ? $"{target}:{port}" : target;
    }

    /// <summary>Parses one entry, or says why it cannot be used.</summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out AllowedTarget? target, out string error)
    {
        target = null;
        error = string.Empty;

        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "An entry is empty.";
            return false;
        }

        if (text.Contains("://", StringComparison.Ordinal) || text.Any(char.IsWhiteSpace))
        {
            error = $"'{text}' is not a host, address or network. Write it without a scheme or path, for example api.internal.example:8443 or 10.0.0.0/16.";
            return false;
        }

        if (!TrySplitPort(text, out var hostPart, out var port, out var bracketed))
        {
            error = $"'{text}' has an unusable port. Use host:port with a port from 1 to 65535, and brackets around an IPv6 address: [fd00::1]:8443.";
            return false;
        }

        if (hostPart.Contains('/'))
        {
            var addressPart = hostPart[..hostPart.IndexOf('/')];
            if (!IPAddress.TryParse(addressPart, out var first))
            {
                error = $"'{text}' is not a host, address or network. Write it without a scheme or path, for example api.internal.example:8443 or 10.0.0.0/16.";
                return false;
            }

            // A network written from an address inside it (10.0.1.0/16) is refused rather than
            // widened: it is as likely a mistyped prefix length as a mistyped address.
            if (!IPNetwork.TryParse(hostPart, out var network) || !IsFullAddress(first, addressPart) || !network.BaseAddress.Equals(first))
            {
                error = $"'{text}' is not a CIDR network. Write the network's first address and its prefix length, for example 10.0.0.0/16.";
                return false;
            }

            target = new AllowedTarget(null, false, network, port);
            return true;
        }

        if (IPAddress.TryParse(hostPart, out var address))
        {
            if (!IsFullAddress(address, hostPart))
            {
                error = $"'{text}' is not a full IP address, such as 10.0.0.5.";
                return false;
            }

            var single = EgressNetworkGuard.Normalize(address);
            target = new AllowedTarget(null, false, new IPNetwork(single, single.AddressFamily == AddressFamily.InterNetwork ? 32 : 128), port);
            return true;
        }

        var wildcard = hostPart.StartsWith("*.", StringComparison.Ordinal);
        var host = DnsHostname.Normalize(wildcard ? hostPart[2..] : hostPart);
        if (bracketed || !DnsHostname.IsValid(host))
        {
            error = wildcard || hostPart.Contains('*')
                ? $"'{text}' is not a usable wildcard. A wildcard is '*.' followed by a domain, for example *.internal.example."
                : $"'{text}' is not a host name, an IP address or a CIDR network.";
            return false;
        }

        target = new AllowedTarget(host, wildcard, null, port);
        return true;
    }

    /// <summary>True when a host name entry covers <paramref name="host"/> (normalized, lower case) on <paramref name="port"/>.</summary>
    public bool MatchesHost(string host, int port) =>
        _host is not null
        && AllowsPort(port)
        && (_wildcard
            ? host.Length > _host.Length + 1 && host.EndsWith("." + _host, StringComparison.Ordinal)
            : string.Equals(host, _host, StringComparison.Ordinal));

    /// <summary>True when an address or network entry contains <paramref name="address"/> on <paramref name="port"/>.</summary>
    public bool MatchesAddress(IPAddress address, int port) =>
        _network is { } network && AllowsPort(port) && network.Contains(EgressNetworkGuard.Normalize(address));

    public bool AllowsPort(int port) => Port is null || Port == port;

    /// <summary>
    /// <see cref="IPAddress.TryParse(string?, out IPAddress?)"/> also reads shorthand such as
    /// <c>10.19</c> as <c>10.0.0.19</c>; an entry must spell an IPv4 address in full.
    /// </summary>
    private static bool IsFullAddress(IPAddress address, string text) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 || text.Count(character => character == '.') == 3;

    /// <summary>Splits off a trailing <c>:port</c>. A bare IPv6 address or network has several colons and no port.</summary>
    private static bool TrySplitPort(string text, out string host, out int? port, out bool bracketed)
    {
        host = text;
        port = null;
        bracketed = text.StartsWith('[');

        if (bracketed)
        {
            var close = text.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            host = text[1..close];
            var tail = text[(close + 1)..];
            return tail.Length == 0 || (tail[0] == ':' && TryPort(tail[1..], out port));
        }

        var separator = text.LastIndexOf(':');
        if (separator < 0 || text.IndexOf(':') != separator)
        {
            return true;
        }

        host = text[..separator];
        return TryPort(text[(separator + 1)..], out port);
    }

    private static bool TryPort(string text, out int? port)
    {
        port = null;
        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed is < 1 or > 65535)
        {
            return false;
        }

        port = parsed;
        return true;
    }
}
