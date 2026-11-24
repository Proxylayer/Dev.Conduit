using System.Net;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// The networks outbound calls may never reach, parsed once from
/// <c>DevConduit:BlockedEgressNetworks</c>. Its main purpose is keeping the cloud instance
/// metadata endpoint out of reach: on a VM with a managed identity that endpoint hands out
/// the app's own credential to anything that asks. Malformed entries are dropped and
/// reported through <see cref="Problems"/> rather than failing the host.
/// </summary>
public sealed class EgressNetworkGuard
{
    private readonly List<IPNetwork> _networks = [];
    private readonly List<string> _problems = [];

    public EgressNetworkGuard(IEnumerable<string?> blockedNetworks)
    {
        ArgumentNullException.ThrowIfNull(blockedNetworks);

        var seen = new HashSet<IPNetwork>();
        foreach (var entry in blockedNetworks)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                _problems.Add("An empty entry was ignored.");
                continue;
            }

            var text = entry.Trim();
            if (!IPNetwork.TryParse(text, out var network))
            {
                _problems.Add($"'{text}' was ignored: it is not a CIDR network such as 169.254.0.0/16.");
                continue;
            }

            // The binder appends configured entries to the built-in defaults, so the same
            // network routinely arrives twice.
            if (seen.Add(network))
            {
                _networks.Add(network);
            }
        }
    }

    /// <summary>The blocked networks in configuration order, without duplicates.</summary>
    public IReadOnlyList<IPNetwork> Networks => _networks;

    /// <summary>Configuration entries that were rejected, with the reason.</summary>
    public IReadOnlyList<string> Problems => _problems;

    public bool IsBlocked(IPAddress address) => IsBlocked(address, _networks);

    /// <summary>
    /// Refuses the connection when any of the addresses <paramref name="host"/> resolved to
    /// is blocked. Any, not all: the connection may be made to whichever address answers.
    /// </summary>
    /// <exception cref="EgressBlockedException">One of the addresses is in a blocked network.</exception>
    public void EnsureAllowed(string host, IEnumerable<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        foreach (var address in addresses)
        {
            if (IsBlocked(address))
            {
                throw new EgressBlockedException(host, address);
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="address"/> falls inside any of <paramref name="networks"/>.
    /// IPv4-mapped IPv6 addresses (::ffff:169.254.169.254) are matched as the IPv4 address
    /// they carry, so the mapped form cannot be used to slip past an IPv4 entry.
    /// </summary>
    public static bool IsBlocked(IPAddress address, IEnumerable<IPNetwork> networks)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(networks);

        var normalized = Normalize(address);
        foreach (var network in networks)
        {
            if (network.Contains(normalized))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Unwraps an IPv4-mapped IPv6 address to plain IPv4; any other address is returned as is.</summary>
    public static IPAddress Normalize(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}
