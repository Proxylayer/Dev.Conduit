using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>
/// The shape of every certificate the factory makes, the signing request round trip and the
/// PKCS#12 form. Everything stays in memory: no store, no file.
/// </summary>
public sealed class CertificateFactoryTests
{
    private const string Host = "devconduit.example.internal";
    private static readonly DateTimeOffset Now = TestCertificates.Now;

    private static T Single<T>(X509Certificate2 certificate)
        where T : X509Extension =>
        Assert.Single(certificate.Extensions.OfType<T>());

    // ---- Server certificate --------------------------------------------------------------

    [Fact]
    public void ServerCertificate_IsEcdsaP256WithSha256()
    {
        using var certificate = CertificateFactory.CreateServerCertificate(Host, Now);

        using var key = certificate.GetECDsaPublicKey();
        Assert.NotNull(key);
        Assert.Equal(256, key.KeySize);
        Assert.Equal("1.2.840.10045.4.3.2", certificate.SignatureAlgorithm.Value);
        Assert.True(certificate.HasPrivateKey);
    }

    [Fact]
    public void ServerCertificate_NamesOnlyTheHostAsItsSubjectAlternativeName()
    {
        using var certificate = CertificateFactory.CreateServerCertificate(Host, Now);

        var names = Single<X509SubjectAlternativeNameExtension>(certificate);
        Assert.Equal([Host], names.EnumerateDnsNames());
        Assert.Empty(names.EnumerateIPAddresses());
        Assert.Equal($"CN={Host}", certificate.Subject);
        Assert.True(certificate.MatchesHostname(Host, allowWildcards: false, allowCommonName: false));
    }

    [Fact]
    public void ServerCertificate_IsForServerAuthenticationOnlyAndNotAnAuthority()
    {
        using var certificate = CertificateFactory.CreateServerCertificate(Host, Now);

        var usage = Single<X509EnhancedKeyUsageExtension>(certificate);
        Assert.Equal([CertificateFactory.ServerAuthOid], usage.EnhancedKeyUsages.Cast<Oid>().Select(oid => oid.Value));

        var constraints = Single<X509BasicConstraintsExtension>(certificate);
        Assert.False(constraints.CertificateAuthority);
        Assert.True(constraints.Critical);
        Assert.False(CertificateFactory.IsAuthority(certificate));

        Assert.Equal(X509KeyUsageFlags.DigitalSignature, Single<X509KeyUsageExtension>(certificate).KeyUsages);
    }

    [Fact]
    public void ServerCertificate_IsBackdatedFiveMinutesAndLastsAYear()
    {
        using var certificate = CertificateFactory.CreateServerCertificate(Host, Now);

        Assert.Equal(Now.AddMinutes(-5).UtcDateTime, certificate.NotBefore.ToUniversalTime());
        Assert.Equal(Now.AddDays(365).UtcDateTime, certificate.NotAfter.ToUniversalTime());
        Assert.Equal(TimeSpan.FromDays(365), CertificateFactory.ServerValidity);
    }

    [Fact]
    public void ServerCertificate_IsSelfSigned()
    {
        using var certificate = CertificateFactory.CreateServerCertificate(Host, Now);

        Assert.Equal(certificate.Subject, certificate.Issuer);
        Assert.True(ServerCertificateRules.IsSelfSigned(certificate));
    }

    [Theory]
    [InlineData("")]
    [InlineData("10.0.0.5")]
    [InlineData("*.example.com")]
    [InlineData("under_score")]
    public void ServerCertificate_RefusesAnythingButAHostName(string hostname) =>
        Assert.Throws<ArgumentException>(() => CertificateFactory.CreateServerCertificate(hostname, Now));

    // ---- Client authority ----------------------------------------------------------------

    [Fact]
    public void ClientAuthority_IsACertificateAuthorityWithPathLengthZero()
    {
        using var authority = CertificateFactory.CreateClientAuthority(Host, Now);

        var constraints = Single<X509BasicConstraintsExtension>(authority);
        Assert.True(constraints.CertificateAuthority);
        Assert.True(constraints.HasPathLengthConstraint);
        Assert.Equal(0, constraints.PathLengthConstraint);
        Assert.True(constraints.Critical);
        Assert.True(CertificateFactory.IsAuthority(authority));
    }

    [Fact]
    public void ClientAuthority_SignsCertificatesOnly()
    {
        using var authority = CertificateFactory.CreateClientAuthority(Host, Now);

        var usage = Single<X509KeyUsageExtension>(authority);
        Assert.True(usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign));
        Assert.False(usage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature));
        Assert.Empty(authority.Extensions.OfType<X509EnhancedKeyUsageExtension>());
        Assert.Equal($"CN={Host} client CA", authority.Subject);
    }

    [Fact]
    public void ClientAuthority_LastsTenYearsFromFiveMinutesAgo()
    {
        using var authority = CertificateFactory.CreateClientAuthority(Host, Now);

        Assert.Equal(Now.AddMinutes(-5).UtcDateTime, authority.NotBefore.ToUniversalTime());
        Assert.Equal(Now.AddDays(3650).UtcDateTime, authority.NotAfter.ToUniversalTime());
    }

    // ---- Client certificates -------------------------------------------------------------

    [Fact]
    public void ClientCertificate_IsForClientAuthenticationAndSignedByTheAuthority()
    {
        using var authority = CertificateFactory.CreateClientAuthority(Host, Now);
        using var client = CertificateFactory.CreateClientCertificate(authority, CertificateFactory.ClientSubject(Host, Now, TestCertificates.Enrollment), Now);

        Assert.True(client.HasPrivateKey);
        Assert.Equal(authority.Subject, client.Issuer);
        Assert.Equal($"CN={Host} client 20260601, OU={TestCertificates.Enrollment}", client.Subject);
        Assert.Equal(TestCertificates.Enrollment, ClientEnrollmentId.Of(client.SubjectName));
        Assert.Equal([CertificateFactory.ClientAuthOid], Single<X509EnhancedKeyUsageExtension>(client).EnhancedKeyUsages.Cast<Oid>().Select(oid => oid.Value));
        Assert.False(CertificateFactory.IsAuthority(client));
        Assert.True(CertificateFactory.HasUsage(client, CertificateFactory.ClientAuthOid));
        Assert.False(CertificateFactory.HasUsage(client, CertificateFactory.ServerAuthOid));

        var authorityKey = Single<X509AuthorityKeyIdentifierExtension>(client);
        var subjectKey = Single<X509SubjectKeyIdentifierExtension>(authority);
        Assert.Equal(subjectKey.SubjectKeyIdentifier, Convert.ToHexString(authorityKey.KeyIdentifier!.Value.Span));

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = Now.UtcDateTime;
        Assert.True(chain.Build(client), string.Join("; ", chain.ChainStatus.Select(status => status.StatusInformation)));
    }

    [Fact]
    public void ClientCertificate_LastsAYearFromFiveMinutesAgo()
    {
        using var authority = CertificateFactory.CreateClientAuthority(Host, Now);
        using var client = CertificateFactory.CreateClientCertificate(authority, CertificateFactory.ClientSubject(Host, Now, TestCertificates.Enrollment), Now);

        Assert.Equal(Now.AddMinutes(-5).UtcDateTime, client.NotBefore.ToUniversalTime());
        Assert.Equal(Now.AddDays(365).UtcDateTime, client.NotAfter.ToUniversalTime());
    }

    [Fact]
    public void ClientCertificate_IsClippedToTheAuthoritysExpiry()
    {
        // An authority made almost ten years ago has 100 days left.
        var authorityMade = Now - CertificateFactory.AuthorityValidity + TimeSpan.FromDays(100);
        using var authority = CertificateFactory.CreateClientAuthority(Host, authorityMade);
        using var client = CertificateFactory.CreateClientCertificate(authority, CertificateFactory.ClientSubject(Host, Now, TestCertificates.Enrollment), Now);

        Assert.Equal(authority.NotAfter, client.NotAfter);
        Assert.True(client.NotAfter.ToUniversalTime() < Now.AddDays(365).UtcDateTime);
    }

    [Fact]
    public void IssueClientCertificate_UsesOnlyTheRequestsSubjectAndKey()
    {
        using var authority = CertificateFactory.CreateClientAuthority(Host, Now);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var subject = CertificateFactory.ClientSubject(Host, Now, TestCertificates.Enrollment);

        // A request asking to be an authority for server authentication.
        var asked = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        asked.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, critical: true));
        asked.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(CertificateFactory.ServerAuthOid)], critical: false));
        var parsed = CertificateRequest.LoadSigningRequest(asked.CreateSigningRequest(), HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default);

        using var issued = CertificateFactory.IssueClientCertificate(authority, parsed, Now);

        Assert.False(issued.HasPrivateKey);
        Assert.Equal(subject.Name, issued.Subject);
        Assert.False(CertificateFactory.IsAuthority(issued));
        Assert.True(CertificateFactory.HasUsage(issued, CertificateFactory.ClientAuthOid));
        Assert.False(CertificateFactory.HasUsage(issued, CertificateFactory.ServerAuthOid));
        using var issuedKey = issued.GetECDsaPublicKey();
        Assert.Equal(key.ExportSubjectPublicKeyInfo(), issuedKey!.ExportSubjectPublicKeyInfo());
    }

    // ---- Signing request -----------------------------------------------------------------

    [Fact]
    public void SigningRequest_RoundTripsSubjectAndKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var subject = CertificateFactory.ClientSubject(Host, Now, TestCertificates.Enrollment);

        var der = CertificateFactory.CreateSigningRequest(subject, key);
        var parsed = CertificateRequest.LoadSigningRequest(der, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default);

        Assert.Equal(subject.RawData, parsed.SubjectName.RawData);
        using var parsedKey = parsed.PublicKey.GetECDsaPublicKey();
        Assert.NotNull(parsedKey);
        Assert.Equal(key.ExportSubjectPublicKeyInfo(), parsedKey.ExportSubjectPublicKeyInfo());
    }

    [Fact]
    public void SigningRequest_WithATamperedSignatureDoesNotLoad()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var der = CertificateFactory.CreateSigningRequest(CertificateFactory.ClientSubject(Host, Now, TestCertificates.Enrollment), key);
        der[^3] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() =>
            CertificateRequest.LoadSigningRequest(der, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default));
    }

    // ---- PKCS#12 -------------------------------------------------------------------------

    [Fact]
    public void Pkcs12_RoundTripsTheCertificateAndItsKey()
    {
        using var original = CertificateFactory.CreateServerCertificate(Host, Now);

        var pkcs12 = CertificateFactory.ExportPkcs12(original);
        using var loaded = CertificateFactory.LoadPkcs12(pkcs12, ephemeral: true);

        Assert.Equal(original.Thumbprint, loaded.Thumbprint);
        Assert.True(loaded.HasPrivateKey);

        // The reloaded key signs what the original public key verifies.
        var data = "round trip"u8.ToArray();
        using var privateKey = loaded.GetECDsaPrivateKey()!;
        using var publicKey = original.GetECDsaPublicKey()!;
        Assert.True(publicKey.VerifyData(data, privateKey.SignData(data, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256));
    }

    [Fact]
    public void Pkcs12_LoadsWithoutAPassword()
    {
        using var authority = CertificateFactory.CreateClientAuthority(Host, Now);
        using var client = CertificateFactory.CreateClientCertificate(authority, CertificateFactory.ClientSubject(Host, Now, TestCertificates.Enrollment), Now);

        using var loaded = X509CertificateLoader.LoadPkcs12(CertificateFactory.ExportPkcs12(client), password: null, X509KeyStorageFlags.EphemeralKeySet);

        Assert.Equal(client.Thumbprint, loaded.Thumbprint);
        Assert.True(loaded.HasPrivateKey);
    }

    // ---- Serial numbers ------------------------------------------------------------------

    [Fact]
    public void NewSerialNumber_IsSixteenBytesPositiveAndNonZeroLeading()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var serial = CertificateFactory.NewSerialNumber();
            Assert.Equal(16, serial.Length);
            Assert.Equal(0, serial[0] & 0x80);
            Assert.NotEqual(0, serial[0]);
        }
    }

    [Fact]
    public void NewSerialNumber_IsUnique()
    {
        var serials = Enumerable.Range(0, 1000).Select(_ => Convert.ToHexString(CertificateFactory.NewSerialNumber())).ToList();

        Assert.Equal(serials.Count, serials.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ClientCertificates_GetDifferentSerials()
    {
        using var authority = CertificateFactory.CreateClientAuthority(Host, Now);
        var subject = CertificateFactory.ClientSubject(Host, Now, TestCertificates.Enrollment);
        using var first = CertificateFactory.CreateClientCertificate(authority, subject, Now);
        using var second = CertificateFactory.CreateClientCertificate(authority, subject, Now);

        Assert.NotEqual(first.SerialNumber, second.SerialNumber);
        Assert.Equal(32, first.SerialNumber.Length);
    }
}
