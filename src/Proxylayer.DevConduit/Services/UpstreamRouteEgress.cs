using Proxylayer.DevConduit.Models;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Server-mode egress: the call goes straight to the upstream the caller named. The
/// caller's Dev.Conduit token has already been consumed by authentication and never travels
/// on; the credential the caller set aside for the upstream is renamed back on the way out.
/// A target the server's allow-list does not cover is refused before any call is built.
/// </summary>
public sealed class UpstreamRouteEgress : IRouteEgress
{
    private readonly TargetAllowList _targets;

    public UpstreamRouteEgress(TargetAllowList targets)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
    }

    public string HttpClientName => ProxyHttpClients.Route;

    /// <summary>
    /// A target <c>DevConduit:Route:AllowedTargets</c> does not cover, answered 403. Anything else
    /// is an upstream this egress can call; the connect step checks the address dialled again.
    /// </summary>
    public RouteResolutionFailure? Refuse(RouteTarget target) =>
        _targets.Allows(target.Origin)
            ? null
            : new RouteResolutionFailure(TargetAllowList.ErrorKind, TargetAllowList.Refusal(target.Origin), StatusCodes.Status403Forbidden);

    public Uri Destination(RouteTarget target, Uri upstreamUri) => upstreamUri;

    public ValueTask ApplyAsync(RouteEgressContext context, CancellationToken cancellationToken)
    {
        var upstreamAuthorization = context.Request.Headers[DevConduitHeaderNames.UpstreamAuthorization].ToString();
        if (!string.IsNullOrEmpty(upstreamAuthorization))
        {
            context.Message.Headers.TryAddWithoutValidation(DevConduitHeaderNames.Authorization, upstreamAuthorization);
            context.Captured.Add(new HeaderItem(
                DevConduitHeaderNames.Authorization, upstreamAuthorization, Enabled: true, IsSecret: true));
        }

        context.Message.Headers.Host = context.Destination.Authority;
        context.Captured.Add(new HeaderItem(DevConduitHeaderNames.Host, context.Destination.Authority));

        return ValueTask.CompletedTask;
    }

    /// <summary>Nothing to explain: every response here came from the upstream itself.</summary>
    public ValueTask<RouteFailure?> ExplainAsync(
        HttpResponseMessage response,
        RouteTarget target,
        CancellationToken cancellationToken) => ValueTask.FromResult<RouteFailure?>(null);
}
