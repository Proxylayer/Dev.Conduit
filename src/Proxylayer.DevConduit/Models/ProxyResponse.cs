namespace Proxylayer.DevConduit.Models;

/// <summary>A single response header (or content header) value.</summary>
public sealed record ResponseHeader(string Name, string Value);

/// <summary>The result of an outbound call that produced an HTTP response.</summary>
public sealed record ProxyResponse(
    int StatusCode,
    string? ReasonPhrase,
    string HttpVersion,
    IReadOnlyList<ResponseHeader> Headers,
    long DurationMs,
    long BodyBytes,
    string? Body,
    bool IsBase64,
    bool BodyTruncated,
    string? ContentType);
