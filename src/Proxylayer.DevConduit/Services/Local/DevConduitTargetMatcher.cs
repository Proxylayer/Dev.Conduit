using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Services.Local;

/// <summary>One effective forward-proxy rule and the remote whose traffic it relays.</summary>
/// <param name="Rule">The host rule.</param>
/// <param name="Remote">The remote Dev.Conduit instance a matching request is relayed to.</param>
/// <param name="Route">The route the rule was derived from, or null for a configured Targets rule.</param>
public sealed record RemoteTargetRule(DevConduitTargetRule Rule, RemoteDefinition Remote, string? Route);

/// <summary>
/// Decides whether a host reached through the forward proxy should be relayed to a remote
/// Dev.Conduit instance rather than contacted directly, and which remote carries it. Immutable
/// and thread-safe once constructed; rules are parsed exactly once.
/// </summary>
public sealed class DevConduitTargetMatcher
{
    private readonly RemoteTargetRule[] _rules;
    private readonly RemoteTargetRule[] _routeRules;
    private readonly RemoteTargetRule[] _targetRules;
    private readonly (string Host, int Port)[] _remoteListeners;
    private readonly int[] _ownPorts;

    /// <summary>
    /// Builds the matcher, reporting unusable rules. The effective rules are every remote's
    /// <c>Targets</c>, in remote order, followed by one rule per usable route origin relayed to
    /// that route's remote, so a host already named by a route is relayed by the forward proxy
    /// without being listed again under Targets.
    /// </summary>
    public DevConduitTargetMatcher(
        RoutingConfiguration routing,
        RouteAliasTable aliases,
        IOptions<DevConduitOptions> options,
        ILogger<DevConduitTargetMatcher> logger)
    {
        var local = options.Value.Local;
        _ownPorts = [local.Port, local.ForwardProxyPort];
        _remoteListeners =
        [
            .. routing.Remotes
                .Where(remote => remote.BaseAddress is not null)
                .Select(remote => (remote.BaseAddress!.Host, remote.BaseAddress.Port))
        ];

        var targetRules = new List<RemoteTargetRule>();
        foreach (var remote in routing.Remotes)
        {
            foreach (var target in remote.Targets)
            {
                if (DevConduitTargetRule.TryParse(target, out var rule))
                {
                    targetRules.Add(new RemoteTargetRule(rule, remote, Route: null));
                }
                else
                {
                    logger.LogWarning(
                        "Ignoring the {Path}:Targets rule '{Rule}': it could not be parsed.",
                        remote.ConfigurationPath,
                        target);
                }
            }
        }

        // Invalid routes are left out of the alias table and reported by the startup
        // diagnostics; a route whose remote is unknown has already stopped local mode.
        var routeRules = new List<(RemoteTargetRule Rule, Uri Origin)>();
        foreach (var route in routing.Routes)
        {
            if (aliases.TryResolve(route.Name, out var origin)
                && routing.RemoteFor(route) is { } remote
                && DevConduitTargetRule.TryParse(OriginRule(origin), out var rule))
            {
                routeRules.Add((new RemoteTargetRule(rule, remote, route.Name), origin));
            }
        }

        _targetRules = [.. targetRules];
        _routeRules = [.. routeRules.Select(entry => entry.Rule)];

        // A route origin adds nothing when an earlier rule already relays it to the same
        // remote, or when an earlier route claims the same origin: the first route wins.
        var effective = new List<RemoteTargetRule>(targetRules);
        foreach (var (routeRule, origin) in routeRules)
        {
            if (!effective.Any(existing => existing.Rule.Matches(origin)
                                           && (existing.Route is not null || SameRemote(existing.Remote, routeRule.Remote))))
            {
                effective.Add(routeRule);
            }
        }

        _rules = [.. effective];
    }

    /// <summary>
    /// The host:port rule for a route origin. The port is always explicit: an origin written
    /// without one, such as https://api.example.com, means the scheme's default port, and a
    /// forward-proxied request to that origin arrives on that port too.
    /// </summary>
    private static string OriginRule(Uri origin) => $"{origin.Host}:{origin.Port}";

    /// <summary>The effective rules, configured targets first, for diagnostics.</summary>
    public IReadOnlyList<DevConduitTargetRule> Rules => [.. _rules.Select(rule => rule.Rule)];

    /// <summary>The effective rules with the remote each relays to, configured targets first.</summary>
    public IReadOnlyList<RemoteTargetRule> RemoteRules => _rules;

    /// <summary>True when traffic for this destination should travel through a remote instance.</summary>
    public bool ShouldRoute(Uri requestUri) => TryMatch(requestUri, out _);

    /// <summary>
    /// The remote a forward-proxied request is relayed to, or false when the request should
    /// go direct.
    /// </summary>
    public bool TryMatch(Uri requestUri, [NotNullWhen(true)] out RemoteDefinition? remote)
    {
        remote = null;

        // Never relay a call already addressed to a Dev.Conduit listener: that is how traffic
        // would loop back into itself forever, and a developer who sets HTTP_PROXY to this
        // process makes exactly that loop reachable.
        if (IsRemoteListener(requestUri) || IsOwnListener(requestUri))
        {
            return false;
        }

        return TryFindRemote(requestUri, out remote);
    }

    /// <summary>
    /// The remote a destination is relayed to by rule: the first route whose origin is that
    /// host and port, then the first remote with a matching <c>Targets</c> rule. A route wins
    /// over a Targets rule, so the forward proxy and <c>/route</c> agree on where a route's
    /// traffic goes.
    /// </summary>
    public bool TryFindRemote(Uri destination, [NotNullWhen(true)] out RemoteDefinition? remote)
    {
        remote = _routeRules.FirstOrDefault(rule => rule.Rule.Matches(destination))?.Remote
                 ?? _targetRules.FirstOrDefault(rule => rule.Rule.Matches(destination))?.Remote;
        return remote is not null;
    }

    private static bool SameRemote(RemoteDefinition left, RemoteDefinition right) =>
        string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);

    private bool IsRemoteListener(Uri requestUri) =>
        _remoteListeners.Any(listener =>
            string.Equals(requestUri.Host, listener.Host, StringComparison.OrdinalIgnoreCase)
            && requestUri.Port == listener.Port);

    private bool IsOwnListener(Uri requestUri)
    {
        if (!_ownPorts.Contains(requestUri.Port))
        {
            return false;
        }

        return string.Equals(requestUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
               || (IPAddress.TryParse(requestUri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
    }
}
