using Proxylayer.DevConduit.Certificates;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>When a client renews its certificate: sixty days before it expires.</summary>
public sealed class ClientCertificateRenewalTests
{
    private static readonly DateTimeOffset NotAfter = new(2027, 6, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RenewWithin_IsSixtyDays() =>
        Assert.Equal(TimeSpan.FromDays(60), ClientCertificateRenewal.RenewWithin);

    [Fact]
    public void RenewAfter_IsSixtyDaysBeforeExpiry() =>
        Assert.Equal(new DateTimeOffset(2027, 4, 2, 0, 0, 0, TimeSpan.Zero), ClientCertificateRenewal.RenewAfter(NotAfter));

    [Fact]
    public void IsDue_IsFalseUpToTheInstantBefore() =>
        Assert.False(ClientCertificateRenewal.IsDue(NotAfter, NotAfter - TimeSpan.FromDays(60) - TimeSpan.FromTicks(1)));

    [Fact]
    public void IsDue_IsTrueFromSixtyDaysBefore()
    {
        Assert.True(ClientCertificateRenewal.IsDue(NotAfter, NotAfter - TimeSpan.FromDays(60)));
        Assert.True(ClientCertificateRenewal.IsDue(NotAfter, NotAfter - TimeSpan.FromDays(1)));
    }

    [Fact]
    public void IsDue_StaysTrueAfterExpiry() =>
        Assert.True(ClientCertificateRenewal.IsDue(NotAfter, NotAfter.AddDays(5)));

    [Fact]
    public void IsDue_IsFalseForAFreshCertificate() =>
        Assert.False(ClientCertificateRenewal.IsDue(NotAfter, NotAfter.AddDays(-365)));

    [Fact]
    public void IsRenewable_IsDueUntilTheInstantItExpires()
    {
        Assert.False(ClientCertificateRenewal.IsRenewable(NotAfter, NotAfter - TimeSpan.FromDays(60) - TimeSpan.FromTicks(1)));
        Assert.True(ClientCertificateRenewal.IsRenewable(NotAfter, NotAfter - TimeSpan.FromDays(60)));
        Assert.True(ClientCertificateRenewal.IsRenewable(NotAfter, NotAfter - TimeSpan.FromTicks(1)));
        Assert.False(ClientCertificateRenewal.IsRenewable(NotAfter, NotAfter));
    }
}
