using Proxylayer.DevConduit.Models;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Everything <see cref="RouteForwarder"/> needs in order to build one outbound call,
/// gathered so an egress does not have to reach back into the pipeline.
/// </summary>
/// <param name="Request">The inbound request as the caller sent it.</param>
/// <param name="Message">The outbound message, with the caller's own headers already copied.</param>
/// <param name="Target">The upstream the caller asked for.</param>
/// <param name="UpstreamUri">The absolute upstream URI the caller's request maps to.</param>
/// <param name="Destination">Where the outbound call is actually addressed.</param>
/// <param name="Captured">Headers to record against the exchange.</param>
public sealed record RouteEgressContext(
    HttpRequest Request,
    HttpRequestMessage Message,
    RouteTarget Target,
    Uri UpstreamUri,
    Uri Destination,
    List<HeaderItem> Captured);

/// <summary>
/// Where a forwarded request is actually sent and what credentials travel with it. Server
/// mode calls the upstream directly; local mode relays the same request to a remote Dev.Conduit
/// instance, which performs the upstream call. Everything else about forwarding — streaming,
/// capture, failure handling — is identical and lives in <see cref="RouteForwarder"/>.
/// </summary>
public interface IRouteEgress
{
    /// <summary>The named <c>HttpClient</c> the outbound call is made on.</summary>
    string HttpClientName { get; }

    /// <summary>
    /// Why this egress cannot carry a resolved target at all, or null when it can. Checked
    /// before any outbound call is built, and answered with 400 like an unresolvable target.
    /// </summary>
    RouteResolutionFailure? Refuse(RouteTarget target);

    /// <summary>The absolute URI to address the outbound call to.</summary>
    Uri Destination(RouteTarget target, Uri upstreamUri);

    /// <summary>Applies the credential and control headers this egress owns.</summary>
    ValueTask ApplyAsync(RouteEgressContext context, CancellationToken cancellationToken);

    /// <summary>
    /// A replacement failure body when the response says something about Dev.Conduit itself
    /// rather than about the upstream, or null to relay the response verbatim. Implementations
    /// may buffer a small body to tell the two apart, never a large one.
    /// </summary>
    ValueTask<RouteFailure?> ExplainAsync(
        HttpResponseMessage response,
        RouteTarget target,
        CancellationToken cancellationToken);
}
