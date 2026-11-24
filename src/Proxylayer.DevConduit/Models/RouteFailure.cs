namespace Proxylayer.DevConduit.Models;

/// <summary>
/// The body returned when <c>/route</c> cannot complete a forward. Unlike the console
/// endpoint, which reports failures inside a 200 for the UI to render, the forwarding
/// endpoint answers with proxy semantics: 400 for a request Dev.Conduit cannot or will not
/// route (including a destination in DevConduit:BlockedEgressNetworks, ErrorKind "Blocked"),
/// 502 for an upstream that could not be reached.
/// </summary>
public sealed record RouteFailure(string ErrorKind, string Error, string? Target = null, string? UpstreamUrl = null);
