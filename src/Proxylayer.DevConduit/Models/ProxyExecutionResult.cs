namespace Proxylayer.DevConduit.Models;

/// <summary>Why an outbound call failed before a response could be read.</summary>
public enum ProxyErrorKind
{
    None = 0,
    InvalidRequest,
    Dns,
    Tls,
    Connection,
    Timeout,
    Canceled,
    Unexpected,

    /// <summary>The destination is in a network listed in DevConduit:BlockedEgressNetworks.</summary>
    Blocked,

    /// <summary>The destination is not covered by DevConduit:Route:AllowedTargets.</summary>
    TargetNotAllowed
}

/// <summary>
/// Outcome of an outbound call: either a response, or a structured failure.
/// Never both, never neither.
/// </summary>
public sealed record ProxyExecutionResult
{
    private ProxyExecutionResult(ProxyResponse? response, string? error, ProxyErrorKind errorKind, long durationMs)
    {
        Response = response;
        Error = error;
        ErrorKind = errorKind;
        DurationMs = durationMs;
    }

    public ProxyResponse? Response { get; }

    public string? Error { get; }

    public ProxyErrorKind ErrorKind { get; }

    public long DurationMs { get; }

    public static ProxyExecutionResult FromResponse(ProxyResponse response) =>
        new(response, null, ProxyErrorKind.None, response.DurationMs);

    public static ProxyExecutionResult FromError(ProxyErrorKind kind, string message, long durationMs) =>
        new(null, message, kind, durationMs);
}
