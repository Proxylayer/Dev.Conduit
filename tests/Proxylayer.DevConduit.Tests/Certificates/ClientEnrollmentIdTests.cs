using System.Security.Cryptography.X509Certificates;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>Enrollment ids: how they are made, and how a client certificate's subject carries one. Pure.</summary>
public sealed class ClientEnrollmentIdTests
{
    private const string Host = "devconduit.example.internal";

    [Fact]
    public void New_IsThirtyTwoLowerCaseHexDigitsAndRandom()
    {
        var first = ClientEnrollmentId.New();
        var second = ClientEnrollmentId.New();

        Assert.Matches("^[0-9a-f]{32}$", first);
        Assert.True(ClientEnrollmentId.IsValid(first));
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("0123456789abcdef0123456789abcde", false)]
    [InlineData("0123456789abcdef0123456789abcdeg", false)]
    [InlineData(" 0123456789abcdef0123456789abcdef", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_AcceptsThirtyTwoHexDigitsOnly(string? id, bool expected) =>
        Assert.Equal(expected, ClientEnrollmentId.IsValid(id));

    [Fact]
    public void ClientSubject_CarriesTheEnrollmentAsItsOrganizationalUnit()
    {
        var subject = CertificateFactory.ClientSubject(Host, TestCertificates.Now, TestCertificates.Enrollment.ToUpperInvariant());

        Assert.Contains($"OU={TestCertificates.Enrollment}", subject.Name);
        Assert.Contains($"CN={Host} client 20260601", subject.Name);
        Assert.Equal(TestCertificates.Enrollment, ClientEnrollmentId.Of(subject));
    }

    [Fact]
    public void ClientSubject_RefusesSomethingNotAnId() =>
        Assert.Throws<ArgumentException>(() => CertificateFactory.ClientSubject(Host, TestCertificates.Now, "not-an-id"));

    [Theory]
    [InlineData("CN=host client")]
    [InlineData("CN=host client, OU=not-an-id")]
    [InlineData("CN=host client, OU=0123456789abcdef0123456789abcdef, OU=fedcba9876543210fedcba9876543210")]
    public void Of_IsNullWithoutExactlyOneValidId(string name) =>
        Assert.Null(ClientEnrollmentId.Of(new X500DistinguishedName(name)));
}
