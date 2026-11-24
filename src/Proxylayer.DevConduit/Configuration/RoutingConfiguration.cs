using System.Diagnostics.CodeAnalysis;

namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// The remote Dev.Conduit instances and named routes read from <c>DevConduit:Remotes</c>,
/// <c>DevConduit:Routes</c> and <c>DevConduit:DefaultRemote</c>. Server mode uses only the
/// routes, as its alias table; local mode uses all of it to decide which remote carries each
/// request. Immutable once built by <see cref="RoutingConfigurationParser.Parse"/>.
/// </summary>
public sealed class RoutingConfiguration
{
    public RoutingConfiguration(
        IReadOnlyList<RemoteDefinition> remotes,
        IReadOnlyList<RouteDefinition> routes,
        RemoteDefinition? defaultRemote,
        IReadOnlyList<string> problems)
    {
        Remotes = remotes;
        Routes = routes;
        DefaultRemote = defaultRemote;
        Problems = problems;
    }

    /// <summary>
    /// The configured remotes, in the order configuration enumerates them: by name, ignoring
    /// case. That order decides which remote's <c>Targets</c> rule wins when two match.
    /// </summary>
    public IReadOnlyList<RemoteDefinition> Remotes { get; }

    /// <summary>The configured routes, by name, as configuration enumerates them.</summary>
    public IReadOnlyList<RouteDefinition> Routes { get; }

    /// <summary>
    /// The remote used when nothing more specific applies: <c>DevConduit:DefaultRemote</c>, or
    /// the only remote when exactly one is configured. Null when there is none.
    /// </summary>
    public RemoteDefinition? DefaultRemote { get; }

    /// <summary>
    /// Problems that stop local mode from starting. Server mode ignores remotes and so
    /// ignores these too; problems with a route's own target are reported by
    /// <c>RouteAliasTable</c> in both modes and never stop the host.
    /// </summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>Finds a remote by name, ignoring case.</summary>
    public bool TryFindRemote(string? name, [NotNullWhen(true)] out RemoteDefinition? remote)
    {
        remote = string.IsNullOrWhiteSpace(name)
            ? null
            : Remotes.FirstOrDefault(candidate => string.Equals(candidate.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        return remote is not null;
    }

    /// <summary>Finds a route by name, ignoring case.</summary>
    public bool TryFindRoute(string? name, [NotNullWhen(true)] out RouteDefinition? route)
    {
        route = string.IsNullOrWhiteSpace(name)
            ? null
            : Routes.FirstOrDefault(candidate => string.Equals(candidate.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        return route is not null;
    }

    /// <summary>The remote that carries a route: the one it names, or the default remote.</summary>
    public RemoteDefinition? RemoteFor(RouteDefinition route) =>
        route.Remote is null
            ? DefaultRemote
            : TryFindRemote(route.Remote, out var remote) ? remote : null;
}
