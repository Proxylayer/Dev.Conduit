using Proxylayer.DevConduit.Models;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// The raw material of one forwarded call, handed from the forwarder to the recorder.
/// Bodies are still byte buffers here: turning them into stored text happens off the
/// response path.
/// </summary>
public sealed record RoutedExchange(
    string UserId,
    DateTimeOffset TimestampUtc,
    string Method,
    string UpstreamUrl,
    string InboundPath,
    string? Alias,
    string TargetOrigin,
    IReadOnlyList<HeaderItem> RequestHeaders,
    CappedBuffer? RequestBody,
    string? RequestContentType,
    int? StatusCode,
    string? ReasonPhrase,
    IReadOnlyList<ResponseHeader> ResponseHeaders,
    CappedBuffer? ResponseBody,
    string? ResponseContentType,
    bool ResponseEncoded,
    long DurationMs,
    string? Error,
    string? ErrorKind);
