using System.Diagnostics.CodeAnalysis;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Services.Local;

/// <summary>
/// Turns a request the developer's application made into the call that carries it to a
/// remote Dev.Conduit instance. Both local ingress styles — the <c>/route</c> reverse proxy and
/// the raw forward proxy — choose a remote, address it and present credentials the same way,
/// so both go through here.
/// </summary>
public sealed class RemoteRelay
{
    /// <summary>The <c>errorKind</c> answered when no remote can carry a <c>/route</c> request.</summary>
    public const string NoRemoteKind = "no_remote";

    private const string RouteSegment = "route";

    private readonly RoutingConfiguration _routing;
    private readonly DevConduitTargetMatcher _matcher;
    private readonly IDevConduitTokenProvider _tokens;

    public RemoteRelay(RoutingConfiguration routing, DevConduitTargetMatcher matcher, IDevConduitTokenProvider tokens)
    {
        if (routing.Remotes.FirstOrDefault(remote => remote.BaseAddress is null) is { } unusable)
        {
            // Startup refuses to run local mode without usable endpoints, so reaching here
            // means the validation that guarantees this invariant was bypassed.
            throw new InvalidOperationException(
                $"{unusable.ConfigurationPath} ('{unusable.Endpoint}') is not a usable remote Dev.Conduit endpoint.");
        }

        _routing = routing;
        _matcher = matcher;
        _tokens = tokens;
    }

    /// <summary>
    /// The remote that carries a <c>/route</c> request: the remote of the route its alias
    /// segment named, else of a route whose origin matches, else of a remote whose
    /// <c>Targets</c> rule matches, else the default remote. False, with the 400 body to
    /// answer, when none applies.
    /// </summary>
    public bool TrySelect(
        RouteTarget target,
        [NotNullWhen(true)] out RemoteDefinition? remote,
        [NotNullWhen(false)] out RouteResolutionFailure? failure)
    {
        failure = null;

        if (target.Alias is { } alias
            && _routing.TryFindRoute(alias, out var route)
            && _routing.RemoteFor(route) is { } routeRemote)
        {
            remote = routeRemote;
            return true;
        }

        if (_matcher.TryFindRemote(target.Origin, out remote))
        {
            return true;
        }

        remote = _routing.DefaultRemote;
        if (remote is not null)
        {
            return true;
        }

        failure = new RouteResolutionFailure(
            NoRemoteKind,
            $"No remote Dev.Conduit is configured for {target.Origin.GetLeftPart(UriPartial.Authority)}. It matches no route "
            + "under DevConduit:Routes and no Targets rule under DevConduit:Remotes, and with several remotes configured "
            + $"({string.Join(", ", _routing.Remotes.Select(candidate => candidate.Name))}) and no DevConduit:DefaultRemote "
            + "there is no default to send it to. Add a Targets rule for this host to one remote, or set DevConduit:DefaultRemote.");
        return false;
    }

    /// <summary>The remote URL that carries a call originally addressed to <paramref name="original"/>.</summary>
    public static Uri RelayUri(RemoteDefinition remote, Uri original) =>
        new(
            remote.BaseAddress ?? throw new InvalidOperationException($"{remote.ConfigurationPath} has no usable endpoint."),
            RouteSegment + original.PathAndQuery);

    /// <summary>
    /// Names the true destination, sets the developer's Entra token for the chosen remote
    /// aside, and preserves any credential the application meant for the final upstream. The
    /// remote renames that one back to <c>Authorization</c> on egress.
    /// </summary>
    public async ValueTask<RelayCredentials> PrepareAsync(
        RemoteDefinition remote,
        Uri origin,
        string? inboundAuthorization,
        string? inboundUpstreamAuthorization,
        CancellationToken cancellationToken)
    {
        // An upstream credential the caller set explicitly wins; otherwise the caller's own
        // Authorization was meant for the upstream, because local mode asks for none itself.
        var upstream = !string.IsNullOrEmpty(inboundUpstreamAuthorization)
            ? inboundUpstreamAuthorization
            : inboundAuthorization;

        var token = await _tokens.GetAccessTokenAsync(remote, cancellationToken).ConfigureAwait(false);

        return new RelayCredentials(
            origin.GetLeftPart(UriPartial.Authority),
            string.IsNullOrEmpty(upstream) ? null : upstream,
            "Bearer " + token);
    }
}

/// <summary>The three header values a relayed call carries to the remote Dev.Conduit instance.</summary>
/// <param name="Target">Value for <c>X-DevConduit-Target</c>.</param>
/// <param name="UpstreamAuthorization">Value for <c>X-DevConduit-Upstream-Authorization</c>, or null.</param>
/// <param name="Authorization">Value for <c>Authorization</c>: the developer's Dev.Conduit token.</param>
public sealed record RelayCredentials(string Target, string? UpstreamAuthorization, string Authorization);
