using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>
/// What <c>/cert</c> accepts as a client certificate: one this server's authority issued,
/// recorded and not revoked, for client authentication and inside its validity period.
/// </summary>
public sealed class ClientCertificateValidatorTests : IDisposable
{
    private const string Host = "devconduit.example.internal";
    private static readonly DateTimeOffset Now = TestCertificates.Now;

    private readonly X509Certificate2 _authority = CertificateFactory.CreateClientAuthority(Host, Now.AddDays(-10));
    private readonly X509Certificate2 _client;
    private readonly IssuedCertificateLedger _ledger = new(
        [],
        [new ClientEnrollment(TestCertificates.Enrollment, "CN=client", Host, "AUTHORITY", Now)]);

    public ClientCertificateValidatorTests()
    {
        _client = CertificateFactory.CreateClientCertificate(_authority, CertificateFactory.ClientSubject(Host, Now, TestCertificates.Enrollment), Now.AddDays(-1));
        _ledger.RecordIssue(Recorded(_client), Now);
    }

    public void Dispose()
    {
        _client.Dispose();
        _authority.Dispose();
    }

    private static IssuedCertificateRecord Recorded(X509Certificate2 certificate) =>
        new(certificate.SerialNumber, certificate.Subject, certificate.NotAfter.ToUniversalTime(), Now, Enrollment: TestCertificates.Enrollment);

    private bool Accepts(X509Certificate2 presented, DateTimeOffset? at = null) =>
        ClientCertificateValidator.IsAccepted(presented, _authority, _ledger, at ?? Now);

    [Fact]
    public void AProperlyIssuedCertificate_IsAccepted() =>
        Assert.True(Accepts(_client));

    [Fact]
    public void ThePresentedCertificateWithoutItsKey_IsAccepted()
    {
        // TLS hands the server the certificate alone.
        using var publicOnly = TestCertificates.PublicOnly(_client);

        Assert.True(Accepts(publicOnly));
    }

    [Fact]
    public void ACertificateFromAForeignAuthority_IsRefused()
    {
        using var foreign = CertificateFactory.CreateClientAuthority(Host, Now.AddDays(-10));
        using var stranger = CertificateFactory.CreateClientCertificate(foreign, CertificateFactory.ClientSubject(Host, Now, TestCertificates.Enrollment), Now.AddDays(-1));
        _ledger.RecordIssue(Recorded(stranger), Now);

        // Recorded, same subject, same authority name; only the signature differs.
        Assert.Equal(_authority.Subject, foreign.Subject);
        Assert.False(Accepts(stranger));
    }

    [Fact]
    public void ACertificateWithoutClientAuth_IsRefused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(CertificateFactory.ClientSubject(Host, Now, TestCertificates.Enrollment), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(CertificateFactory.ServerAuthOid)], critical: false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(_authority, true, false));
        using var serverOnly = request.Create(_authority, Now.AddDays(-1), Now.AddDays(100), CertificateFactory.NewSerialNumber());
        _ledger.RecordIssue(Recorded(serverOnly), Now);

        Assert.False(Accepts(serverOnly));
    }

    [Fact]
    public void AnAuthorityCertificate_IsRefusedEvenWithClientAuth()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(CertificateFactory.ClientSubject(Host, Now, TestCertificates.Enrollment), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(CertificateFactory.ClientAuthOid)], critical: false));
        using var subAuthority = request.Create(_authority, Now.AddDays(-1), Now.AddDays(100), CertificateFactory.NewSerialNumber());
        _ledger.RecordIssue(Recorded(subAuthority), Now);

        Assert.False(Accepts(subAuthority));
    }

    [Fact]
    public void AnExpiredCertificate_IsRefused()
    {
        var expiry = _client.NotAfter.ToUniversalTime();

        Assert.True(Accepts(_client, new DateTimeOffset(expiry).AddSeconds(-1)));
        Assert.False(Accepts(_client, new DateTimeOffset(expiry)));
        Assert.False(Accepts(_client, new DateTimeOffset(expiry).AddDays(1)));
    }

    [Fact]
    public void ACertificateNotYetValid_IsRefused() =>
        Assert.False(Accepts(_client, new DateTimeOffset(_client.NotBefore.ToUniversalTime()).AddSeconds(-1)));

    [Fact]
    public void AnUnknownSerial_IsRefused()
    {
        // Signed by the right authority, but never recorded.
        using var unrecorded = CertificateFactory.CreateClientCertificate(_authority, CertificateFactory.ClientSubject(Host, Now, TestCertificates.Enrollment), Now.AddDays(-1));

        Assert.False(Accepts(unrecorded));
    }

    [Fact]
    public void ARevokedSerial_IsRefused()
    {
        Assert.NotNull(_ledger.Revoke(_client.SerialNumber, "test", Now));

        Assert.False(Accepts(_client));
    }

    [Fact]
    public void ARenewalOfARevokedEnrollment_IsRefused()
    {
        using var renewal = CertificateFactory.CreateClientCertificate(_authority, CertificateFactory.ClientSubject(Host, Now, TestCertificates.Enrollment), Now);
        _ledger.RecordIssue(Recorded(renewal), Now);
        _ledger.MarkSuperseded(_client.SerialNumber, renewal.SerialNumber);

        _ledger.Revoke(_client.SerialNumber, "test", Now);

        Assert.False(Accepts(_client));
        Assert.False(Accepts(renewal));
    }

    [Fact]
    public void ACertificateWithoutAnEnrollmentInItsSubject_IsRefused()
    {
        var subject = new X500DistinguishedName($"CN={Host} client");
        using var plain = CertificateFactory.CreateClientCertificate(_authority, subject, Now.AddDays(-1));
        _ledger.RecordIssue(Recorded(plain), Now);

        Assert.False(Accepts(plain));
    }

    [Fact]
    public void ACertificateNamingAnotherEnrollmentThanItsRecord_IsRefused()
    {
        const string other = "fedcba9876543210fedcba9876543210";
        using var mislabelled = CertificateFactory.CreateClientCertificate(_authority, CertificateFactory.ClientSubject(Host, Now, other), Now.AddDays(-1));
        _ledger.RecordIssue(Recorded(mislabelled), Now);

        Assert.False(Accepts(mislabelled));
    }

    [Fact]
    public void ASupersededCertificate_IsAcceptedUntilItExpires()
    {
        using var renewal = CertificateFactory.CreateClientCertificate(_authority, CertificateFactory.ClientSubject(Host, Now, TestCertificates.Enrollment), Now);
        _ledger.RecordIssue(Recorded(renewal), Now);
        _ledger.MarkSuperseded(_client.SerialNumber, renewal.SerialNumber);

        Assert.True(Accepts(_client));
        Assert.True(Accepts(renewal));
        Assert.False(Accepts(_client, new DateTimeOffset(_client.NotAfter.ToUniversalTime())));
    }

    [Fact]
    public void TheAuthorityItself_IsRefused()
    {
        _ledger.RecordIssue(Recorded(_authority), Now);

        Assert.False(Accepts(_authority));
    }

    [Fact]
    public void AServerCertificate_IsRefused()
    {
        using var server = CertificateFactory.CreateServerCertificate(Host, Now.AddDays(-1));
        _ledger.RecordIssue(Recorded(server), Now);

        Assert.False(Accepts(server));
    }
}
