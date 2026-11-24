using System.Net;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Models;

namespace Proxylayer.DevConduit.Services.Local;

/// <summary>
/// Local-mode egress: the target is resolved here, from the same route table server mode
/// uses, a remote is chosen for it, and the request is then handed to that remote Dev.Conduit
/// instance, which performs the upstream call. The developer's application supplies no token and no headers; this is
/// where the Entra credential joins the request.
/// </summary>
public sealed class RemoteRelayRouteEgress : IRouteEgress
{
    private readonly RemoteRelay _relay;

    public RemoteRelayRouteEgress(RemoteRelay relay)
    {
        _relay = relay;
    }

    /// <summary>
    /// A client that ignores HTTP_PROXY. A developer running local mode has very likely
    /// pointed HTTP_PROXY at this process, and honouring it here would relay the relay.
    /// </summary>
    public string HttpClientName => ProxyHttpClients.LocalRelay;

    /// <summary>Refuses a target no remote is configured to carry.</summary>
    public RouteResolutionFailure? Refuse(RouteTarget target) =>
        _relay.TrySelect(target, out _, out var failure) ? null : failure;

    public Uri Destination(RouteTarget target, Uri upstreamUri) => RemoteRelay.RelayUri(RemoteFor(target), upstreamUri);

    public async ValueTask ApplyAsync(RouteEgressContext context, CancellationToken cancellationToken)
    {
        var remote = RemoteFor(context.Target);
        context.Captured.Add(new HeaderItem(DevConduitHeaderNames.Remote, remote.Name, Enabled: false));

        var credentials = await _relay
            .PrepareAsync(
                remote,
                context.UpstreamUri,
                context.Request.Headers[DevConduitHeaderNames.Authorization].ToString(),
                context.Request.Headers[DevConduitHeaderNames.UpstreamAuthorization].ToString(),
                cancellationToken)
            .ConfigureAwait(false);

        var message = context.Message;
        message.Headers.TryAddWithoutValidation(DevConduitHeaderNames.Target, credentials.Target);
        context.Captured.Add(new HeaderItem(DevConduitHeaderNames.Target, credentials.Target));

        if (credentials.UpstreamAuthorization is { } upstream)
        {
            message.Headers.TryAddWithoutValidation(DevConduitHeaderNames.UpstreamAuthorization, upstream);
            context.Captured.Add(new HeaderItem(
                DevConduitHeaderNames.UpstreamAuthorization, upstream, Enabled: true, IsSecret: true));
        }

        message.Headers.TryAddWithoutValidation(DevConduitHeaderNames.Authorization, credentials.Authorization);
        context.Captured.Add(new HeaderItem(
            DevConduitHeaderNames.Authorization, credentials.Authorization, Enabled: true, IsSecret: true));

        message.Headers.Host = context.Destination.Authority;
        context.Captured.Add(new HeaderItem(DevConduitHeaderNames.Host, context.Destination.Authority));
    }

    /// <summary>
    /// A 401 or 403 raised by the remote instance is about the developer's token, not about
    /// the upstream they were trying to reach, and relaying it verbatim would send them
    /// looking at the wrong end of the chain. An upstream's own 401 or 403 still comes back
    /// untouched, so the two are told apart by the shape of the refusal rather than by status
    /// alone: Dev.Conduit challenges with an empty bearer 401, and refuses with a small JSON
    /// body naming one of its own error kinds.
    /// </summary>
    public async ValueTask<RouteFailure?> ExplainAsync(
        HttpResponseMessage response,
        RouteTarget target,
        CancellationToken cancellationToken)
    {
        var origin = target.Origin.GetLeftPart(UriPartial.Authority);
        var remote = RemoteFor(target);

        if (response.StatusCode == HttpStatusCode.Unauthorized && IsBearerChallenge(response))
        {
            return new RouteFailure(
                "remote_unauthorized",
                $"The remote Dev.Conduit '{remote.Name}' at {remote.BaseAddress} rejected the access token this machine "
                + $"acquired for it. Check that the tenant and client id this machine signs in with for '{remote.Name}' "
                + $"({remote.DescribeIdentity()}; {remote.ConfigurationPath}:TenantId and :ClientId override AzureAd) "
                + "name the same app registration that remote validates against, then delete the token cache to force "
                + "a fresh sign-in. This is not a failure of the upstream you were calling.",
                origin);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden
            && await NamesDevConduitRefusalAsync(response, cancellationToken).ConfigureAwait(false))
        {
            return new RouteFailure(
                "remote_forbidden",
                $"The remote Dev.Conduit '{remote.Name}' at {remote.BaseAddress} accepted the token but refused the call. "
                + $"Either the token is missing the delegated scope that remote requires — {remote.ConfigurationPath}:Scopes "
                + $"(now {string.Join(", ", remote.Scopes)}) must name api://<client-id>/DevConduit.Forward, and the app "
                + "registration must expose it and have consent — or the signed-in account is not on that remote's "
                + "DevConduit:AllowedUserOids or DevConduit:AllowedGroupIds list. This is not a failure of the upstream you "
                + "were calling.",
                origin);
        }

        return null;
    }

    /// <summary>
    /// The remote chosen for a target. <see cref="Refuse"/> has already answered 400 for a
    /// target with none, so reaching here without one means that check was bypassed.
    /// </summary>
    private RemoteDefinition RemoteFor(RouteTarget target) =>
        _relay.TrySelect(target, out var remote, out var failure)
            ? remote
            : throw new InvalidOperationException(failure.Message);

    /// <summary>True for the empty bearer challenge Dev.Conduit answers an unauthenticated call with.</summary>
    private static bool IsBearerChallenge(HttpResponseMessage response) =>
        response.Content.Headers.ContentLength is null or 0
        && response.Headers.WwwAuthenticate.Any(header =>
            string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when a small JSON body names one of Dev.Conduit's own refusal kinds. Buffering is
    /// bounded to a few kilobytes so a large upstream payload is never held in memory.
    /// </summary>
    private static async ValueTask<bool> NamesDevConduitRefusalAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        const int MaxInspectedBytes = 8 * 1024;

        if (response.Content.Headers.ContentLength is > MaxInspectedBytes)
        {
            return false;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            // ReadAsStringAsync buffers the content, so the relay path can still read it.
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return body.Contains("insufficient_scope", StringComparison.OrdinalIgnoreCase)
                   || body.Contains("not_allowed", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
