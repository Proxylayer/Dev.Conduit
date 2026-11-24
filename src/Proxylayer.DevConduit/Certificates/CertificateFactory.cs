using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>
/// Creates every certificate Dev.Conduit uses, all ECDSA P-256 with SHA-256: the server's
/// self-signed end-entity certificate, the private authority that signs client certificates,
/// and the client certificates themselves. Every validity period starts five minutes before
/// <c>now</c>, to absorb clock skew between server and client. Nothing here touches a
/// certificate store or the disk.
/// </summary>
public static class CertificateFactory
{
    public const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    public const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    public static readonly TimeSpan ServerValidity = TimeSpan.FromDays(365);
    public static readonly TimeSpan ClientValidity = TimeSpan.FromDays(365);
    public static readonly TimeSpan AuthorityValidity = TimeSpan.FromDays(3650);

    private static readonly TimeSpan Backdate = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The server's certificate: self-signed, CA=false, server authentication only, with the
    /// host name as its only subject alternative name. The private key is exportable, so the
    /// caller can store it as a PKCS#12 blob.
    /// </summary>
    public static X509Certificate2 CreateServerCertificate(string hostname, DateTimeOffset now)
    {
        RequireHostname(hostname);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = new CertificateRequest(CommonName(hostname), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthOid)], critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(hostname);
        request.CertificateExtensions.Add(names.Build());

        return request.CreateSelfSigned(now - Backdate, now + ServerValidity);
    }

    /// <summary>
    /// The private authority that signs client certificates and nothing else: CA=true with a
    /// path length of zero, certificate signing only. It is never exported or installed.
    /// </summary>
    public static X509Certificate2 CreateClientAuthority(string hostname, DateTimeOffset now)
    {
        RequireHostname(hostname);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = new CertificateRequest(CommonName($"{hostname} client CA"), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        return request.CreateSelfSigned(now - Backdate, now + AuthorityValidity);
    }

    /// <summary>
    /// The subject of the client certificates of one enrollment, created by an export on
    /// <paramref name="now"/>: the host name and date as the common name, and the enrollment
    /// id as the one organizational unit, so <c>/cert</c> can read it from the certificate.
    /// </summary>
    public static X500DistinguishedName ClientSubject(string hostname, DateTimeOffset now, string enrollment)
    {
        if (!ClientEnrollmentId.IsValid(enrollment))
        {
            throw new ArgumentException($"'{enrollment}' is not an enrollment id.", nameof(enrollment));
        }

        var builder = new X500DistinguishedNameBuilder();
        builder.AddCommonName($"{hostname} client {now.UtcDateTime:yyyyMMdd}");
        builder.AddOrganizationalUnitName(ClientEnrollmentId.Normalize(enrollment));
        return builder.Build();
    }

    /// <summary>A new client certificate and key pair, signed by <paramref name="authority"/>. The key is exportable.</summary>
    public static X509Certificate2 CreateClientCertificate(X509Certificate2 authority, X500DistinguishedName subject, DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        using var certificate = SignClientCertificate(authority, request, now);
        return certificate.CopyWithPrivateKey(key);
    }

    /// <summary>
    /// A client certificate for the public key in a certificate signing request. The caller has
    /// already checked the request; only its subject and public key are used, never its
    /// extensions, which the authority sets itself.
    /// </summary>
    public static X509Certificate2 IssueClientCertificate(X509Certificate2 authority, CertificateRequest signingRequest, DateTimeOffset now)
    {
        var request = new CertificateRequest(signingRequest.SubjectName, signingRequest.PublicKey, HashAlgorithmName.SHA256);
        return SignClientCertificate(authority, request, now);
    }

    /// <summary>A PKCS#10 signing request for <paramref name="key"/> under <paramref name="subject"/>, DER encoded.</summary>
    public static byte[] CreateSigningRequest(X500DistinguishedName subject, ECDsa key) =>
        new CertificateRequest(subject, key, HashAlgorithmName.SHA256).CreateSigningRequest();

    /// <summary>
    /// Reloads a certificate from its PKCS#12 form. Windows TLS cannot use the ephemeral keys
    /// <see cref="CertificateRequest"/> creates, so a certificate goes through PKCS#12 before
    /// it is served or presented.
    /// </summary>
    public static X509Certificate2 LoadPkcs12(byte[] pkcs12, bool ephemeral = false) =>
        X509CertificateLoader.LoadPkcs12(
            pkcs12,
            password: null,
            ephemeral ? X509KeyStorageFlags.EphemeralKeySet : X509KeyStorageFlags.DefaultKeySet);

    /// <summary>The certificate and its private key as PKCS#12 with no password.</summary>
    public static byte[] ExportPkcs12(X509Certificate2 certificate) => certificate.Export(X509ContentType.Pkcs12);

    /// <summary>A positive serial number of 16 random bytes.</summary>
    public static byte[] NewSerialNumber()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        serial[0] |= 0x01;
        return serial;
    }

    /// <summary>True when the certificate's enhanced key usage names <paramref name="oid"/>.</summary>
    public static bool HasUsage(X509Certificate2 certificate, string oid) =>
        certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(usage => usage.Value == oid));

    /// <summary>True when basic constraints say this is a certificate authority.</summary>
    public static bool IsAuthority(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority);

    private static X509Certificate2 SignClientCertificate(X509Certificate2 authority, CertificateRequest request, DateTimeOffset now)
    {
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ClientAuthOid)], critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(
            authority, includeKeyIdentifier: true, includeIssuerAndSerial: false));

        // A certificate may not outlive the authority that signed it.
        var notAfter = now + ClientValidity;
        if (notAfter > authority.NotAfter)
        {
            notAfter = authority.NotAfter;
        }

        return request.Create(authority, now - Backdate, notAfter, NewSerialNumber());
    }

    private static X500DistinguishedName CommonName(string name)
    {
        var builder = new X500DistinguishedNameBuilder();
        builder.AddCommonName(name);
        return builder.Build();
    }

    private static void RequireHostname(string hostname)
    {
        if (!DnsHostname.IsValid(hostname))
        {
            throw new ArgumentException($"'{hostname}' is not a DNS host name.", nameof(hostname));
        }
    }
}
