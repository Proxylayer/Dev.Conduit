using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>
/// How the relay validates a remote Dev.Conduit's TLS certificate. A certificate Windows already
/// trusts for the host is accepted as always. Otherwise it is accepted only when its
/// thumbprint is pinned for that host by a <c>.conduit</c> import or a later sync, it is
/// inside its validity period, and it names the host. A host nothing was imported for gets
/// Windows validation alone, unchanged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RemoteCertificatePolicy
{
    private readonly ClientTrustStore _store;
    private readonly TimeProvider _clock;

    public RemoteCertificatePolicy(ClientTrustStore store, TimeProvider clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>A <see cref="RemoteCertificateValidationCallback"/>; the host comes from the TLS stream.</summary>
    public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        Accepts((sender as SslStream)?.TargetHostName, certificate, errors);

    public bool Accepts(string? hostname, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (string.IsNullOrEmpty(hostname) || certificate is null)
        {
            return false;
        }

        // Chain and name errors are what pinning answers; a missing certificate is not.
        if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
        {
            return false;
        }

        return IsPinned(hostname, certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()), _store.PinsFor(hostname), _clock.GetUtcNow());
    }

    /// <summary>The pinning rule on its own: pinned thumbprint, inside its validity period, naming the host.</summary>
    public static bool IsPinned(string hostname, X509Certificate2 leaf, IReadOnlyList<X509Certificate2> pins, DateTimeOffset now) =>
        pins.Any(pin => string.Equals(pin.Thumbprint, leaf.Thumbprint, StringComparison.OrdinalIgnoreCase))
        && now >= leaf.NotBefore.ToUniversalTime()
        && now < leaf.NotAfter.ToUniversalTime()
        && leaf.MatchesHostname(hostname, allowWildcards: false, allowCommonName: false);
}
