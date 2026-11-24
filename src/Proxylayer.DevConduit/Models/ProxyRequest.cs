using System.Text.Json.Serialization;

namespace Proxylayer.DevConduit.Models;

/// <summary>An outbound HTTP call as composed by the developer.</summary>
public sealed record ProxyRequest(
    string Method,
    string Url,
    IReadOnlyList<HeaderItem> Headers,
    string? Body = null,
    string? ContentType = null,
    int? TimeoutSeconds = null,
    bool FollowRedirects = false,
    bool AllowInvalidCertificates = false)
{
    public const int DefaultTimeoutSeconds = 100;
    public const int MaxTimeoutSeconds = 600;

    /// <summary>Timeout clamped into a sane range.</summary>
    [JsonIgnore]
    public int EffectiveTimeoutSeconds =>
        TimeoutSeconds is null or <= 0
            ? DefaultTimeoutSeconds
            : Math.Min(TimeoutSeconds.Value, MaxTimeoutSeconds);
}
