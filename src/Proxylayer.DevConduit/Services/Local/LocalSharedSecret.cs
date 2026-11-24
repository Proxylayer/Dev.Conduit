using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Services.Local;

/// <summary>
/// The optional second lock on local-mode proxy ingress. Loopback already limits reach to
/// this machine; a shared secret narrows that further to the processes a developer chose to
/// tell it to. Unset — the default — means loopback is the only control.
/// </summary>
public sealed class LocalSharedSecret
{
    private readonly byte[]? _expected;

    public LocalSharedSecret(IOptions<DevConduitOptions> options)
    {
        var configured = options.Value.Local.SharedSecret;
        _expected = string.IsNullOrEmpty(configured) ? null : Encoding.UTF8.GetBytes(configured);
    }

    /// <summary>True when a secret is configured and inbound requests must present it.</summary>
    public bool IsRequired => _expected is not null;

    /// <summary>Explains what a caller has to send. Used in the 401 body.</summary>
    public string Requirement =>
        $"This Dev.Conduit is running in local mode with DevConduit:Local:SharedSecret set. Send "
        + $"'{DevConduitHeaderNames.LocalSecret}: <the configured value>' on every proxied request.";

    /// <summary>True when the presented value matches, compared without an early exit.</summary>
    public bool Accepts(string? presented)
    {
        if (_expected is null)
        {
            return true;
        }

        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected);
    }
}
