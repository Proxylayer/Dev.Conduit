using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>Merging pinned server certificates, dropping expired ones, and loading pins back as certificates.</summary>
public sealed class PinnedTrustTests
{
    private const string Host = "devconduit.example.internal";
    private static readonly DateTimeOffset Now = TestCertificates.Now;

    private static PublishedServerCertificate Pin(string thumbprint, DateTimeOffset notAfter, string der = "AA==") =>
        new(der, notAfter.AddDays(-365), notAfter, thumbprint);

    [Fact]
    public void Pin_CarriesTheDerTimesAndThumbprint()
    {
        using var certificate = CertificateFactory.CreateServerCertificate(Host, Now);

        var pin = PinnedTrust.Pin(certificate);

        Assert.Equal(certificate.RawData, Convert.FromBase64String(pin.Der));
        Assert.Equal(certificate.Thumbprint, pin.Thumbprint);
        Assert.Equal(Now.AddMinutes(-5), pin.NotBefore);
        Assert.Equal(Now.AddDays(365), pin.NotAfter);
    }

    [Fact]
    public void Merge_KeepsExistingAndOfferedOncePerThumbprint()
    {
        var merged = PinnedTrust.Merge(
            [Pin("AAAA", Now.AddDays(100)), Pin("BBBB", Now.AddDays(200))],
            [Pin("bbbb", Now.AddDays(200)), Pin("CCCC", Now.AddDays(300))],
            Now);

        Assert.Equal(["CCCC", "BBBB", "AAAA"], merged.Select(pin => pin.Thumbprint));
    }

    [Fact]
    public void Merge_UpperCasesThumbprints()
    {
        var merged = PinnedTrust.Merge([], [Pin("abcd", Now.AddDays(1))], Now);

        Assert.Equal("ABCD", Assert.Single(merged).Thumbprint);
    }

    [Fact]
    public void Merge_DropsExpiredPinsFromBothSides()
    {
        var merged = PinnedTrust.Merge(
            [Pin("OLD1", Now), Pin("KEEP", Now.AddSeconds(1))],
            [Pin("OLD2", Now.AddDays(-1))],
            Now);

        Assert.Equal(["KEEP"], merged.Select(pin => pin.Thumbprint));
    }

    [Fact]
    public void Merge_OrdersNewestExpiryFirst()
    {
        var merged = PinnedTrust.Merge([Pin("A", Now.AddDays(10)), Pin("B", Now.AddDays(30))], [Pin("C", Now.AddDays(20))], Now);

        Assert.Equal(["B", "C", "A"], merged.Select(pin => pin.Thumbprint));
    }

    [Fact]
    public void Merge_PrefersTheExistingEntryForADuplicateThumbprint()
    {
        var merged = PinnedTrust.Merge([Pin("AAAA", Now.AddDays(10), der: "ZXhpc3Rpbmc=")], [Pin("AAAA", Now.AddDays(10), der: "b2ZmZXJlZA==")], Now);

        Assert.Equal("ZXhpc3Rpbmc=", Assert.Single(merged).Der);
    }

    [Fact]
    public void Merge_OfNothingIsEmpty() =>
        Assert.Empty(PinnedTrust.Merge([], [], Now));

    [Fact]
    public void Load_ReturnsEachPinThatParsesAndMatchesItsThumbprint()
    {
        using var first = CertificateFactory.CreateServerCertificate(Host, Now);
        using var second = CertificateFactory.CreateServerCertificate(Host, Now);

        var loaded = PinnedTrust.Load([PinnedTrust.Pin(first), PinnedTrust.Pin(second)]);

        Assert.Equal([first.Thumbprint, second.Thumbprint], loaded.Select(certificate => certificate.Thumbprint));
        Assert.All(loaded, certificate => Assert.False(certificate.HasPrivateKey));
        foreach (var certificate in loaded)
        {
            certificate.Dispose();
        }
    }

    [Fact]
    public void Load_SkipsAPinWhoseThumbprintDisagrees()
    {
        using var certificate = CertificateFactory.CreateServerCertificate(Host, Now);
        var lying = PinnedTrust.Pin(certificate) with { Thumbprint = new string('0', 40) };

        Assert.Empty(PinnedTrust.Load([lying]));
    }

    [Fact]
    public void Load_MatchesTheThumbprintIgnoringCase()
    {
        using var certificate = CertificateFactory.CreateServerCertificate(Host, Now);
        var lower = PinnedTrust.Pin(certificate) with { Thumbprint = certificate.Thumbprint.ToLowerInvariant() };

        using var loaded = Assert.Single(PinnedTrust.Load([lower]));
        Assert.Equal(certificate.Thumbprint, loaded.Thumbprint);
    }

    [Fact]
    public void Load_SkipsDamagedEntriesAndKeepsTheRest()
    {
        using var certificate = CertificateFactory.CreateServerCertificate(Host, Now);

        var loaded = PinnedTrust.Load(
        [
            Pin("X", Now.AddDays(1), der: "not base64!"),
            Pin("Y", Now.AddDays(1), der: Convert.ToBase64String([1, 2, 3])),
            PinnedTrust.Pin(certificate)
        ]);

        using var only = Assert.Single(loaded);
        Assert.Equal(certificate.Thumbprint, only.Thumbprint);
    }

    [Fact]
    public void Load_KeepsAnExpiredPin()
    {
        // Load parses; dropping expired pins is Merge's job and the policy's time check.
        using var expired = TestCertificates.SelfSigned(Host, [Host], notBefore: Now.AddDays(-30), notAfter: Now.AddDays(-1));

        using var loaded = Assert.Single(PinnedTrust.Load([PinnedTrust.Pin(expired)]));
        Assert.Equal(expired.Thumbprint, loaded.Thumbprint);
    }
}
