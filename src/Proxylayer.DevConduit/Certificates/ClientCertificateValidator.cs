using System.Security.Cryptography.X509Certificates;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>
/// Decides whether a client certificate presented to <c>/cert</c> is one this server issued
/// and still honours: it chains to the client authority and to nothing else, carries the
/// client authentication usage, is inside its validity period, names its enrollment in its
/// subject, and its serial is recorded in the ledger under that enrollment, which is not
/// revoked. A superseded certificate is accepted until it expires. Windows trust plays no part.
/// </summary>
public static class ClientCertificateValidator
{
    public static bool IsAccepted(
        X509Certificate2 presented,
        X509Certificate2 authority,
        IssuedCertificateLedger ledger,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(presented);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(ledger);

        if (now < presented.NotBefore.ToUniversalTime() || now >= presented.NotAfter.ToUniversalTime())
        {
            return false;
        }

        if (!CertificateFactory.HasUsage(presented, CertificateFactory.ClientAuthOid) || CertificateFactory.IsAuthority(presented))
        {
            return false;
        }

        if (ClientEnrollmentId.Of(presented.SubjectName) is not { } enrollment
            || !ledger.IsAccepted(presented.SerialNumber, enrollment))
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid(CertificateFactory.ClientAuthOid));

        try
        {
            if (!chain.Build(presented) || chain.ChainElements.Count != 2)
            {
                return false;
            }

            return chain.ChainElements[1].Certificate.RawData.AsSpan().SequenceEqual(authority.RawData);
        }
        finally
        {
            foreach (var element in chain.ChainElements)
            {
                element.Certificate.Dispose();
            }
        }
    }
}
