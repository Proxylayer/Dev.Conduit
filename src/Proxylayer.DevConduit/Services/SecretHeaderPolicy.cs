using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Models;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Decides which headers count as secrets and produces history-safe copies of them.
/// Secret values live on in saved header sets, but never in history files.
/// </summary>
public sealed class SecretHeaderPolicy
{
    public const string RedactedValue = "***redacted***";

    private readonly HashSet<string> _secretNames;

    public SecretHeaderPolicy(IOptions<DevConduitOptions> options)
    {
        _secretNames = new HashSet<string>(options.Value.SecretHeaderNames, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsSecret(HeaderItem header) => header.IsSecret || _secretNames.Contains(header.Name);

    public bool IsSecretName(string name) => _secretNames.Contains(name);

    /// <summary>Returns a history-safe copy of a response header.</summary>
    public ResponseHeader Redact(ResponseHeader header) =>
        IsSecretName(header.Name)
            ? header with { Value = RedactedValue }
            : header;

    /// <summary>Returns a copy of the request whose secret header values are redacted.</summary>
    public ProxyRequest Redact(ProxyRequest request) =>
        request with { Headers = request.Headers.Select(Redact).ToArray() };

    public HeaderItem Redact(HeaderItem header) =>
        IsSecret(header)
            ? header with { Value = RedactedValue, IsSecret = true }
            : header;
}
