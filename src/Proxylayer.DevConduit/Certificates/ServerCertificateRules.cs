using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>
/// What a client accepts as a Dev.Conduit server certificate before pinning it or adding it to
/// Windows trust: self-signed with a valid signature, not a certificate authority, for server
/// authentication, and naming exactly the server's host name. An authority certificate is
/// refused outright, since trusting one would trust everything it signs.
/// </summary>
public static class ServerCertificateRules
{
    /// <summary>Why <paramref name="certificate"/> is not acceptable for <paramref name="hostname"/>, or null when it is.</summary>
    public static string? Problem(X509Certificate2 certificate, string hostname)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        if (CertificateFactory.IsAuthority(certificate))
        {
            return $"The server certificate {certificate.Thumbprint} is a certificate authority (CA=true), which is never trusted.";
        }

        if (!CertificateFactory.HasUsage(certificate, CertificateFactory.ServerAuthOid))
        {
            return $"The server certificate {certificate.Thumbprint} is not for server authentication.";
        }

        if (!certificate.MatchesHostname(hostname, allowWildcards: false, allowCommonName: false))
        {
            return $"The server certificate {certificate.Thumbprint} does not name {hostname} among its subject alternative names.";
        }

        if (!IsSelfSigned(certificate))
        {
            return $"The server certificate {certificate.Thumbprint} is not self-signed.";
        }

        return null;
    }

    /// <summary>True when the certificate's own key verifies its signature.</summary>
    public static bool IsSelfSigned(X509Certificate2 certificate)
    {
        if (!certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData))
        {
            return false;
        }

        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(publicOnly);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(CertificateFactory.ServerAuthOid));

        try
        {
            return chain.Build(publicOnly) && chain.ChainElements.Count == 1;
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
