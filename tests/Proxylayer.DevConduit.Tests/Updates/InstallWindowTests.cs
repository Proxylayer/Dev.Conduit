using Proxylayer.DevConduit.Tests.Support;
using Proxylayer.DevConduit.Updates;

namespace Proxylayer.DevConduit.Tests.Updates;

/// <summary>The service's quiet hour, with a fake clock and explicit time zones.</summary>
public sealed class InstallWindowTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static readonly TimeZoneInfo PlusTwo =
        TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 3, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Next_BeforeTheHour_IsTheHourToday() =>
        Assert.Equal(At(10, 3), InstallWindow.Next(At(10, 1, 15), 3, Utc));

    [Fact]
    public void Next_InsideTheHour_IsNow() =>
        Assert.Equal(At(10, 3, 40), InstallWindow.Next(At(10, 3, 40), 3, Utc));

    [Fact]
    public void Next_AfterTheHour_IsTheHourTomorrow() =>
        Assert.Equal(At(11, 3), InstallWindow.Next(At(10, 4, 0), 3, Utc));

    [Fact]
    public void Next_FollowsTheLocalZone()
    {
        // 03:00 at UTC+2 is 01:00 UTC.
        var next = InstallWindow.Next(At(10, 0, 30), 3, PlusTwo);

        Assert.Equal(At(10, 1), next.ToUniversalTime());
        Assert.Equal(3, next.Hour);
    }

    [Fact]
    public void Next_ANotBeforeInsideTheWindowIsThatMoment() =>
        Assert.Equal(At(11, 3, 10), InstallWindow.Next(At(10, 9), 3, Utc, notBefore: At(11, 3, 10)));

    [Fact]
    public void Next_ANotBeforeAfterTheWindowWaitsAnotherDay() =>
        Assert.Equal(At(12, 3), InstallWindow.Next(At(10, 9), 3, Utc, notBefore: At(11, 5)));

    [Fact]
    public void Next_ARetryADayAfterAFailedAttemptLandsInTheNextDaysWindow()
    {
        var failedAt = At(10, 3, 0);

        var next = InstallWindow.Next(At(10, 3, 5), 3, Utc, notBefore: failedAt + InstallWindow.RetryAfterFailure);

        Assert.Equal(At(11, 3, 0), next);
    }

    [Fact]
    public void Next_ANotBeforeInThePastChangesNothing() =>
        Assert.Equal(At(10, 3), InstallWindow.Next(At(10, 2), 3, Utc, notBefore: At(1, 0)));

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(30, 23)]
    public void Next_ClampsTheHour(int hour, int expected) =>
        Assert.Equal(expected, InstallWindow.Next(At(10, 0, 0).AddMinutes(-30), hour, Utc).Hour);

    [Fact]
    public void Next_AnHourSkippedByAClockChangeStartsAtTheNextValidTime()
    {
        // Clocks go from 02:00 to 03:00 on the last Sunday of March; 02:30 does not exist that day.
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2000, 1, 1),
            new DateTime(2099, 12, 31),
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test DST", TimeSpan.Zero, "Test DST", "Test", "Test Summer", [rule]);

        // 29 March 2026 is the last Sunday in March.
        var next = InstallWindow.Next(new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero), 2, zone);

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 3, 0, 0, TimeSpan.FromHours(1)), next);
    }

    [Fact]
    public void IsDue_OnceTheClockReachesTheTime()
    {
        var clock = new ManualClock(At(10, 2, 59));
        var due = InstallWindow.Next(clock.GetUtcNow(), 3, Utc);

        Assert.False(InstallWindow.IsDue(clock.GetUtcNow(), due));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(InstallWindow.IsDue(clock.GetUtcNow(), due));
    }

    // ---- What the history says about the last attempt ------------------------------------

    [Fact]
    public void History_AnAttemptAtTheRunningVersionInstalled()
    {
        var history = new UpdateHistory(new UpdateAttempt("1.2.0", "1.1.0", At(10, 3), @"C:\x\install-1.2.0.log"), null, null);

        Assert.Equal(UpdateOutcome.Installed, history.Outcome(new Version(1, 2, 0)));
        Assert.Equal(UpdateOutcome.Installed, history.Outcome(new Version(1, 3, 0)));
        Assert.Equal(UpdateOutcome.Failed, history.Outcome(new Version(1, 1, 0)));
        Assert.Equal(UpdateOutcome.None, UpdateHistory.Empty.Outcome(new Version(1, 1, 0)));
    }

    [Fact]
    public void History_RetriesOnlyTheFailedVersionADayLater()
    {
        var history = new UpdateHistory(null, "1.2.0", At(10, 3));

        Assert.Equal(At(11, 3), history.RetryNotBefore(new Version(1, 2, 0)));
        Assert.Null(history.RetryNotBefore(new Version(1, 2, 1)));
        Assert.Null(UpdateHistory.Empty.RetryNotBefore(new Version(1, 2, 0)));
    }
}
