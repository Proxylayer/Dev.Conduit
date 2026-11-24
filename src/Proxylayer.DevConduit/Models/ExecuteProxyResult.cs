namespace Proxylayer.DevConduit.Models;

/// <summary>What <c>POST /api/proxy/execute</c> returns: a response, or a structured failure.</summary>
public sealed record ExecuteProxyResult(
    ProxyResponse? Response,
    string? Error,
    string? ErrorKind,
    long DurationMs,
    string HistoryId);
