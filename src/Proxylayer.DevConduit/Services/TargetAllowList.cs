using System.Net;
using System.Net.Sockets;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// The hosts a server may call, from <c>DevConduit:Route:AllowedTargets</c>, plus the target of
/// every configured route, which is always allowed. An empty list allows every host that is not
/// in <c>DevConduit:BlockedEgressNetworks</c>; a list with any entry, even one that cannot be
/// used, allows only what it covers. Malformed entries are dropped and reported through
/// <see cref="Problems"/> rather than failing the host, as <see cref="RouteAliasTable"/> does.
/// </summary>
/// <remarks>
/// Checked twice. On the target a request names (<see cref="Allows"/>): a host name must match a
/// host name entry, or there must be a network entry its addresses might fall in. And at connect
/// time on the address actually dialled (<see cref="EnsureConnectionAllowed"/>): a host name
/// entry or route target allows the name whatever it resolves to, and otherwise every resolved
/// address must lie in a network entry, so a name that resolves, or rebinds, outside the
/// networks is refused. Immutable and thread-safe once built.
/// </remarks>
public sealed class TargetAllowList
{
    public const string SettingKey = "DevConduit:Route:AllowedTargets";

    /// <summary>The <c>errorKind</c> <c>/route</c> answers with when it refuses a target.</summary>
    public const string ErrorKind = "target_not_allowed";

    private readonly List<AllowedTarget> _entries = [];
    private readonly List<string> _problems = [];
    private readonly HashSet<(string Host, int Port)> _routeTargets = [];

    /// <param name="entries">The configured entries; blank ones are ignored, as for every list setting.</param>
    /// <param name="routeTargets">The origins of the configured routes, allowed implicitly.</param>
    public TargetAllowList(IEnumerable<string?> entries, IEnumerable<Uri> routeTargets)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(routeTargets);

        foreach (var entry in entries.Where(entry => !string.IsNullOrWhiteSpace(entry)))
        {
            IsRestricted = true;
            if (AllowedTarget.TryParse(entry, out var target, out var error))
            {
                _entries.Add(target);
            }
            else
            {
                _problems.Add($"{error} The entry was ignored.");
            }
        }

        foreach (var origin in routeTargets)
        {
            _routeTargets.Add((NormalizeHost(origin.Host), origin.Port));
        }
    }

    /// <summary>A list that allows every host: an empty setting, and always in local mode.</summary>
    public static TargetAllowList Unrestricted { get; } = new([], []);

    /// <summary>
    /// The list a process runs with: the server's setting and its routes' targets. Local mode
    /// relays to its remote, which applies its own list, so a workstation is never restricted.
    /// </summary>
    public static TargetAllowList For(DevConduitOptions options, RouteAliasTable routes)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(routes);

        return options.Mode == DevConduitMode.Local
            ? Unrestricted
            : new TargetAllowList(options.Route.AllowedTargets, routes.Origins);
    }

    /// <summary>True when the setting names anything, so only what it covers, and the route targets, may be called.</summary>
    public bool IsRestricted { get; }

    /// <summary>The usable entries in configuration order.</summary>
    public IReadOnlyList<AllowedTarget> Entries => _entries;

    /// <summary>Configuration entries that were rejected, with the reason.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>
    /// Whether a request may name <paramref name="origin"/>. A host name with no host name entry
    /// passes while a network entry might cover its addresses; the connect-time check decides.
    /// </summary>
    public bool Allows(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!IsRestricted)
        {
            return true;
        }

        var host = NormalizeHost(origin.Host);
        var port = origin.Port;
        if (_routeTargets.Contains((host, port)))
        {
            return true;
        }

        if (TryParseAddress(host, out var address))
        {
            return _entries.Any(entry => entry.MatchesAddress(address, port));
        }

        return _entries.Any(entry => entry.MatchesHost(host, port) || (entry.IsNetwork && entry.AllowsPort(port)));
    }

    /// <summary>Whether a connection to <paramref name="host"/>, resolved to <paramref name="addresses"/>, may be made.</summary>
    public bool AllowsConnection(string host, int port, IReadOnlyCollection<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(addresses);
        if (!IsRestricted)
        {
            return true;
        }

        var name = NormalizeHost(host);
        if (_routeTargets.Contains((name, port)))
        {
            return true;
        }

        if (!TryParseAddress(name, out _) && _entries.Any(entry => entry.MatchesHost(name, port)))
        {
            return true;
        }

        // Every address, not any: the connection may be made to whichever one answers.
        return addresses.Count > 0 && addresses.All(address => _entries.Any(entry => entry.MatchesAddress(address, port)));
    }

    /// <exception cref="TargetNotAllowedException">The list does not cover the connection.</exception>
    public void EnsureConnectionAllowed(string host, int port, IReadOnlyCollection<IPAddress> addresses)
    {
        if (!AllowsConnection(host, port, addresses))
        {
            throw new TargetNotAllowedException(host, port, addresses);
        }
    }

    /// <summary>The refusal for a request naming <paramref name="origin"/>.</summary>
    public static string Refusal(Uri origin) =>
        $"The target '{origin.GetLeftPart(UriPartial.Authority)}' is not covered by {SettingKey}, and no route names it. "
        + "Ask the server's administrator to add it, or use a configured route.";

    /// <summary>A host as compared here: no brackets or trailing dot, lower case, an address in its canonical form.</summary>
    private static string NormalizeHost(string host)
    {
        var trimmed = host.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        return IPAddress.TryParse(trimmed, out var address) ? EgressNetworkGuard.Normalize(address).ToString() : trimmed;
    }

    private static bool TryParseAddress(string host, out IPAddress address)
    {
        address = IPAddress.None;
        if (!IPAddress.TryParse(host, out var parsed)
            || (parsed.AddressFamily != AddressFamily.InterNetworkV6 && host.Count(character => character == '.') != 3))
        {
            return false;
        }

        address = parsed;
        return true;
    }
}
