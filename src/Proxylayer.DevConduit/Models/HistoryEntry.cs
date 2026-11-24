namespace Proxylayer.DevConduit.Models;

/// <summary>A full record of one executed request, stored as its own JSON file.</summary>
public sealed record HistoryEntry(
    string Id,
    DateTimeOffset TimestampUtc,
    ProxyRequest Request,
    ProxyResponse? Response,
    string? Error,
    string? ErrorKind,
    long RequestBytes,
    bool RequestBodyTruncated)
{
    /// <summary>Allocates a filesystem-safe identifier for a new entry.</summary>
    public static string NewId() => Guid.NewGuid().ToString("n");
}

/// <summary>The lightweight projection kept in the per-user history index.</summary>
public sealed record HistorySummary(
    string Id,
    DateTimeOffset TimestampUtc,
    string Method,
    string Url,
    int? StatusCode,
    long DurationMs,
    long RequestBytes,
    long ResponseBytes,
    string? Error);
