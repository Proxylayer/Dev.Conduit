namespace Proxylayer.DevConduit.Models;

/// <summary>
/// A full record of one call forwarded through <c>/route</c>, stored as its own JSON file.
/// Bodies are captured up to <c>DevConduit:MaxRoutedStoredBodyBytes</c>; secret headers are
/// stored redacted.
/// </summary>
public sealed record RoutedEntry(
    string Id,
    DateTimeOffset TimestampUtc,
    string Method,
    string Url,
    string InboundPath,
    string? Alias,
    string TargetOrigin,
    IReadOnlyList<HeaderItem> RequestHeaders,
    string? RequestBody,
    bool RequestBodyIsBase64,
    bool RequestBodyTruncated,
    long RequestBytes,
    string? RequestContentType,
    int? StatusCode,
    string? ReasonPhrase,
    IReadOnlyList<ResponseHeader> ResponseHeaders,
    string? ResponseBody,
    bool ResponseBodyIsBase64,
    bool ResponseBodyTruncated,
    long ResponseBytes,
    string? ResponseContentType,
    long DurationMs,
    string? Error,
    string? ErrorKind)
{
    /// <summary>Allocates a filesystem-safe identifier for a new entry.</summary>
    public static string NewId() => Guid.NewGuid().ToString("n");
}
