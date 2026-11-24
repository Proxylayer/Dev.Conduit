using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>
/// What a client pins or puts into Windows trust as a server certificate: self-signed with a
/// good signature, not an authority, for server authentication, naming exactly the host.
/// </summary>
public sealed class ServerCertificateRulesTests
{
    private const string Host = "devconduit.example.internal";

    [Fact]
    public void TheFactorysServerCertificate_IsAccepted()
    {
        using var certificate = CertificateFactory.CreateServerCertificate(Host, TestCertificates.Now);

        Assert.Null(ServerCertificateRules.Problem(certificate, Host));
    }

    [Fact]
    public void ACertificateWithoutItsKey_IsAccepted()
    {
        using var certificate = CertificateFactory.CreateServerCertificate(Host, TestCertificates.Now);
        using var publicOnly = TestCertificates.PublicOnly(certificate);

        Assert.Null(ServerCertificateRules.Problem(publicOnly, Host));
    }

    [Fact]
    public void AHandMadeCertificateOfTheRightShape_IsAccepted()
    {
        using var certificate = TestCertificates.SelfSigned(Host, [Host]);

        Assert.Null(ServerCertificateRules.Problem(certificate, Host));
    }

    [Fact]
    public void AnExpiredCertificate_PassesTheRulesWhichDoNotCheckTime()
    {
        // Expiry is the caller's check; the rules are about shape and signature only.
        using var certificate = TestCertificates.SelfSigned(
            Host, [Host], notBefore: TestCertificates.Now.AddDays(-400), notAfter: TestCertificates.Now.AddDays(-1));

        Assert.Null(ServerCertificateRules.Problem(certificate, Host));
    }

    [Fact]
    public void ACertificateAuthority_IsRefused()
    {
        using var certificate = TestCertificates.SelfSigned(Host, [Host], authority: true);

        Assert.Contains("CA=true", ServerCertificateRules.Problem(certificate, Host));
    }

    [Fact]
    public void TheClientAuthority_IsRefused()
    {
        using var authority = CertificateFactory.CreateClientAuthority(Host, TestCertificates.Now);

        Assert.Contains("certificate authority", ServerCertificateRules.Problem(authority, Host));
    }

    [Fact]
    public void AWrongSubjectAlternativeName_IsRefused()
    {
        using var certificate = CertificateFactory.CreateServerCertificate("other.devconduit.internal", TestCertificates.Now);

        Assert.Contains($"does not name {Host}", ServerCertificateRules.Problem(certificate, Host));
    }

    [Fact]
    public void AWildcardName_IsRefused()
    {
        using var certificate = TestCertificates.SelfSigned("*.devconduit.internal", ["*.devconduit.internal"]);

        Assert.Contains("does not name", ServerCertificateRules.Problem(certificate, Host));
    }

    [Fact]
    public void ACommonNameWithoutASubjectAlternativeName_IsRefused()
    {
        using var certificate = TestCertificates.SelfSigned(Host, dnsNames: null);

        Assert.Contains("does not name", ServerCertificateRules.Problem(certificate, Host));
    }

    [Fact]
    public void AnExtraNameBesideTheHost_IsAccepted()
    {
        // Exactly the host must be among the names; others alongside it are not refused.
        using var certificate = TestCertificates.SelfSigned(Host, [Host, "other.example"]);

        Assert.Null(ServerCertificateRules.Problem(certificate, Host));
    }

    [Fact]
    public void ABadSignature_IsRefused()
    {
        using var certificate = TestCertificates.SelfSigned(Host, [Host], signWithAnotherKey: true);

        Assert.Equal(certificate.Subject, certificate.Issuer);
        Assert.False(ServerCertificateRules.IsSelfSigned(certificate));
        Assert.Contains("not self-signed", ServerCertificateRules.Problem(certificate, Host));
    }

    [Fact]
    public void ACertificateSignedByAnAuthority_IsRefused()
    {
        using var authority = CertificateFactory.CreateClientAuthority(Host, TestCertificates.Now);
        using var client = CertificateFactory.CreateClientCertificate(authority, CertificateFactory.ClientSubject(Host, TestCertificates.Now, TestCertificates.Enrollment), TestCertificates.Now);

        Assert.False(ServerCertificateRules.IsSelfSigned(client));
        Assert.NotNull(ServerCertificateRules.Problem(client, Host));
    }

    [Fact]
    public void AMissingEnhancedKeyUsage_IsRefused()
    {
        using var certificate = TestCertificates.SelfSigned(Host, [Host], withUsageExtension: false);

        Assert.Contains("not for server authentication", ServerCertificateRules.Problem(certificate, Host));
    }

    [Fact]
    public void ClientAuthenticationOnly_IsRefused()
    {
        using var certificate = TestCertificates.SelfSigned(Host, [Host], usages: [CertificateFactory.ClientAuthOid]);

        Assert.Contains("not for server authentication", ServerCertificateRules.Problem(certificate, Host));
    }

    [Fact]
    public void HostComparison_IgnoresCase()
    {
        using var certificate = CertificateFactory.CreateServerCertificate(Host, TestCertificates.Now);

        Assert.Null(ServerCertificateRules.Problem(certificate, Host.ToUpperInvariant()));
    }
}
