using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Proxylayer.DevConduit.Certificates;

namespace Proxylayer.DevConduit.Tests.Support;

/// <summary>
/// Certificates the production factory never makes, for the rejection cases: an authority
/// posing as a server, wrong or wildcard names, no subject alternative name, a signature
/// another key made, no enhanced key usage. All in memory; nothing touches a store.
/// </summary>
internal static class TestCertificates
{
    public static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>An enrollment id for client certificates made outside a ledger.</summary>
    public const string Enrollment = "0123456789abcdef0123456789abcdef";

    /// <summary>A self-signed P-256 certificate shaped like a server certificate, with each part switchable.</summary>
    public static X509Certificate2 SelfSigned(
        string commonName,
        string[]? dnsNames = null,
        bool authority = false,
        string[]? usages = null,
        bool withUsageExtension = true,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        bool signWithAnotherKey = false)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var subject = new X500DistinguishedName($"CN={commonName}");
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(authority, false, 0, critical: true));
        if (withUsageExtension)
        {
            var oids = new OidCollection();
            foreach (var usage in usages ?? [CertificateFactory.ServerAuthOid])
            {
                oids.Add(new Oid(usage));
            }

            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, critical: false));
        }

        if (dnsNames is { Length: > 0 })
        {
            var names = new SubjectAlternativeNameBuilder();
            foreach (var name in dnsNames)
            {
                names.AddDnsName(name);
            }

            request.CertificateExtensions.Add(names.Build());
        }

        var from = notBefore ?? Now.AddDays(-1);
        var to = notAfter ?? Now.AddDays(300);

        if (!signWithAnotherKey)
        {
            return request.CreateSelfSigned(from, to);
        }

        // Issuer name equals the subject, but the signature is made by an unrelated key.
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var publicOnly = request.Create(subject, X509SignatureGenerator.CreateForECDsa(other), from, to, CertificateFactory.NewSerialNumber());
        return publicOnly.CopyWithPrivateKey(key);
    }

    /// <summary>The certificate without its private key, as a peer would see it.</summary>
    public static X509Certificate2 PublicOnly(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadCertificate(certificate.RawData);
}
