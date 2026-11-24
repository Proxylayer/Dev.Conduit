using Proxylayer.DevConduit.Certificates;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>
/// Each rotation decision at its boundaries, against a fixed clock: issue the next certificate
/// inside sixty days, switch to it inside thirty, replace at once when there is nothing valid
/// to switch to, and replace on a host name change.
/// </summary>
public sealed class ServerCertificateRotationTests
{
    private const string Host = "devconduit.example.internal";
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A certificate with <paramref name="remaining"/> left at <see cref="Now"/>.</summary>
    private static ServerCertificateFacts Expiring(TimeSpan remaining, string hostname = Host, string thumbprint = "CURRENT") =>
        new(thumbprint, hostname, Now + remaining - TimeSpan.FromDays(365), Now + remaining);

    private static ServerCertificateFacts FreshNext(string hostname = Host) =>
        new("NEXT", hostname, Now - TimeSpan.FromMinutes(5), Now + TimeSpan.FromDays(365));

    [Fact]
    public void Windows_AreSixtyAndThirtyDays()
    {
        Assert.Equal(TimeSpan.FromDays(60), ServerCertificateRotation.IssueNextWithin);
        Assert.Equal(TimeSpan.FromDays(30), ServerCertificateRotation.SwitchWithin);
    }

    // ---- No certificate yet --------------------------------------------------------------

    [Fact]
    public void NoCertificate_IsReplaceCurrent()
    {
        var plan = ServerCertificateRotation.Plan(Host, null, null, Now);

        Assert.Equal(ServerCertificateStep.ReplaceCurrent, plan.Step);
        Assert.False(plan.HostnameChanged);
    }

    [Fact]
    public void NoCurrentButAValidNext_IsPromoteNext() =>
        Assert.Equal(ServerCertificateStep.PromoteNext, ServerCertificateRotation.Plan(Host, null, FreshNext(), Now).Step);

    [Fact]
    public void NoCurrentAndANextForAnotherHost_IsReplaceCurrent() =>
        Assert.Equal(ServerCertificateStep.ReplaceCurrent, ServerCertificateRotation.Plan(Host, null, FreshNext("other.example"), Now).Step);

    // ---- None ----------------------------------------------------------------------------

    [Theory]
    [InlineData(365)]
    [InlineData(100)]
    [InlineData(61)]
    public void PlentyLeft_IsNone(int days) =>
        Assert.Equal(ServerCertificateStep.None, ServerCertificateRotation.Plan(Host, Expiring(TimeSpan.FromDays(days)), null, Now).Step);

    [Fact]
    public void ExactlySixtyDaysLeft_IsStillNone() =>
        Assert.Equal(ServerCertificateStep.None, ServerCertificateRotation.Plan(Host, Expiring(TimeSpan.FromDays(60)), null, Now).Step);

    [Fact]
    public void HostnameComparison_IgnoresCase() =>
        Assert.Equal(
            ServerCertificateStep.None,
            ServerCertificateRotation.Plan(Host.ToUpperInvariant(), Expiring(TimeSpan.FromDays(200)), null, Now).Step);

    // ---- IssueNext -----------------------------------------------------------------------

    [Fact]
    public void JustUnderSixtyDaysLeft_IsIssueNext() =>
        Assert.Equal(
            ServerCertificateStep.IssueNext,
            ServerCertificateRotation.Plan(Host, Expiring(TimeSpan.FromDays(60) - TimeSpan.FromSeconds(1)), null, Now).Step);

    [Fact]
    public void ExactlyThirtyDaysLeft_IsIssueNext() =>
        Assert.Equal(ServerCertificateStep.IssueNext, ServerCertificateRotation.Plan(Host, Expiring(TimeSpan.FromDays(30)), null, Now).Step);

    [Fact]
    public void UnderSixtyDaysWithAValidNext_IsNone() =>
        Assert.Equal(ServerCertificateStep.None, ServerCertificateRotation.Plan(Host, Expiring(TimeSpan.FromDays(45)), FreshNext(), Now).Step);

    [Fact]
    public void UnderSixtyDaysWithANextForAnotherHost_IsIssueNext() =>
        Assert.Equal(
            ServerCertificateStep.IssueNext,
            ServerCertificateRotation.Plan(Host, Expiring(TimeSpan.FromDays(45)), FreshNext("other.example"), Now).Step);

    [Fact]
    public void UnderSixtyDaysWithAnExpiredNext_IsIssueNext()
    {
        var expiredNext = new ServerCertificateFacts("NEXT", Host, Now.AddDays(-400), Now.AddDays(-1));

        Assert.Equal(ServerCertificateStep.IssueNext, ServerCertificateRotation.Plan(Host, Expiring(TimeSpan.FromDays(45)), expiredNext, Now).Step);
    }

    // ---- PromoteNext ---------------------------------------------------------------------

    [Fact]
    public void JustUnderThirtyDaysWithAValidNext_IsPromoteNext() =>
        Assert.Equal(
            ServerCertificateStep.PromoteNext,
            ServerCertificateRotation.Plan(Host, Expiring(TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1)), FreshNext(), Now).Step);

    [Fact]
    public void ExactlyThirtyDaysWithAValidNext_IsNone() =>
        Assert.Equal(ServerCertificateStep.None, ServerCertificateRotation.Plan(Host, Expiring(TimeSpan.FromDays(30)), FreshNext(), Now).Step);

    [Fact]
    public void ExpiredWithAValidNext_IsPromoteNext()
    {
        var expired = Expiring(TimeSpan.Zero);

        var plan = ServerCertificateRotation.Plan(Host, expired, FreshNext(), Now);

        Assert.Equal(ServerCertificateStep.PromoteNext, plan.Step);
        Assert.Contains("outside its validity period", plan.Reason);
    }

    [Fact]
    public void NotYetValidWithAValidNext_IsPromoteNext()
    {
        var future = new ServerCertificateFacts("CURRENT", Host, Now.AddSeconds(1), Now.AddDays(365));

        Assert.Equal(ServerCertificateStep.PromoteNext, ServerCertificateRotation.Plan(Host, future, FreshNext(), Now).Step);
    }

    [Fact]
    public void ANextNotYetValid_IsNotUsable()
    {
        var futureNext = new ServerCertificateFacts("NEXT", Host, Now.AddSeconds(1), Now.AddDays(365));

        Assert.Equal(
            ServerCertificateStep.ReplaceCurrent,
            ServerCertificateRotation.Plan(Host, Expiring(TimeSpan.FromDays(10)), futureNext, Now).Step);
    }

    // ---- ReplaceCurrent ------------------------------------------------------------------

    [Fact]
    public void UnderThirtyDaysWithoutANext_IsReplaceCurrent()
    {
        var plan = ServerCertificateRotation.Plan(Host, Expiring(TimeSpan.FromDays(10)), null, Now);

        Assert.Equal(ServerCertificateStep.ReplaceCurrent, plan.Step);
        Assert.Contains("no next certificate was published in time", plan.Reason);
    }

    [Fact]
    public void ExpiredWithoutANext_IsReplaceCurrent() =>
        Assert.Equal(ServerCertificateStep.ReplaceCurrent, ServerCertificateRotation.Plan(Host, Expiring(TimeSpan.FromDays(-1)), null, Now).Step);

    [Fact]
    public void ExpiringAtThisInstant_IsOutsideItsValidity() =>
        Assert.Contains(
            "outside its validity period",
            ServerCertificateRotation.Plan(Host, Expiring(TimeSpan.Zero), null, Now).Reason);

    [Fact]
    public void NotYetValidWithoutANext_IsReplaceCurrent()
    {
        var future = new ServerCertificateFacts("CURRENT", Host, Now.AddMinutes(1), Now.AddDays(365));

        Assert.Equal(ServerCertificateStep.ReplaceCurrent, ServerCertificateRotation.Plan(Host, future, null, Now).Step);
    }

    [Fact]
    public void StartingAtThisInstant_IsValid()
    {
        var starting = new ServerCertificateFacts("CURRENT", Host, Now, Now.AddDays(365));

        Assert.Equal(ServerCertificateStep.None, ServerCertificateRotation.Plan(Host, starting, null, Now).Step);
    }

    // ---- Host name change ----------------------------------------------------------------

    [Fact]
    public void AnotherHostname_IsReplaceCurrentFlaggedAsAHostnameChange()
    {
        var plan = ServerCertificateRotation.Plan("new-host.example", Expiring(TimeSpan.FromDays(300)), null, Now);

        Assert.Equal(ServerCertificateStep.ReplaceCurrent, plan.Step);
        Assert.True(plan.HostnameChanged);
        Assert.Contains($"from {Host} to new-host.example", plan.Reason);
    }

    [Fact]
    public void AnotherHostname_WinsOverAValidNextForTheNewHost()
    {
        var plan = ServerCertificateRotation.Plan("new-host.example", Expiring(TimeSpan.FromDays(10)), FreshNext("new-host.example"), Now);

        Assert.Equal(ServerCertificateStep.ReplaceCurrent, plan.Step);
        Assert.True(plan.HostnameChanged);
    }

    // ---- Facts ---------------------------------------------------------------------------

    [Fact]
    public void IsValidFor_IsHalfOpenAndCaseInsensitive()
    {
        var facts = new ServerCertificateFacts("T", Host, Now, Now.AddDays(1));

        Assert.True(facts.IsValidFor(Host.ToUpperInvariant(), Now));
        Assert.True(facts.IsValidFor(Host, Now.AddDays(1).AddTicks(-1)));
        Assert.False(facts.IsValidFor(Host, Now.AddDays(1)));
        Assert.False(facts.IsValidFor(Host, Now.AddTicks(-1)));
        Assert.False(facts.IsValidFor("other.example", Now.AddHours(1)));
    }

    /// <summary>Following the plan step by step always ends at None, as Converge relies on.</summary>
    [Theory]
    [InlineData(400)]
    [InlineData(100)]
    [InlineData(45)]
    [InlineData(10)]
    [InlineData(-5)]
    public void ApplyingEachStep_SettlesWithinThreeSteps(int daysLeft)
    {
        ServerCertificateFacts? current = Expiring(TimeSpan.FromDays(daysLeft));
        ServerCertificateFacts? next = null;
        var steps = 0;

        while (true)
        {
            var plan = ServerCertificateRotation.Plan(Host, current, next, Now);
            if (plan.Step == ServerCertificateStep.None)
            {
                break;
            }

            switch (plan.Step)
            {
                case ServerCertificateStep.ReplaceCurrent:
                    current = FreshNext() with { Thumbprint = $"NEW{steps}" };
                    next = null;
                    break;
                case ServerCertificateStep.IssueNext:
                    next = FreshNext();
                    break;
                case ServerCertificateStep.PromoteNext:
                    current = next;
                    next = null;
                    break;
            }

            Assert.True(++steps <= 3, $"Still rotating after {steps} steps.");
        }
    }
}
