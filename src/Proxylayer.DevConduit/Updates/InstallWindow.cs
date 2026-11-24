namespace Proxylayer.DevConduit.Updates;

/// <summary>
/// The service's quiet hour: the hour starting at <c>InstallHour</c>, local time, in which a
/// verified release is installed. Pure: the time and zone are passed in, so a test can use any.
/// </summary>
public static class InstallWindow
{
    /// <summary>How long after a failed attempt the next one may start.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromDays(1);

    /// <summary>
    /// When to install: now, when now falls in a quiet hour and is not before
    /// <paramref name="notBefore"/>; otherwise the start of the next quiet hour that allows it (or
    /// <paramref name="notBefore"/> itself when it falls inside one).
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="installHour">The local hour, 0 to 23, the window starts at; clamped.</param>
    /// <param name="zone">The local time zone.</param>
    /// <param name="notBefore">The earliest allowed time, such as a day after a failed attempt; null for none.</param>
    public static DateTimeOffset Next(DateTimeOffset now, int installHour, TimeZoneInfo zone, DateTimeOffset? notBefore = null)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var hour = Math.Clamp(installHour, 0, 23);
        var earliest = notBefore is { } limit && limit > now ? limit : now;

        var localDay = TimeZoneInfo.ConvertTime(earliest, zone).Date;
        for (var day = localDay.AddDays(-1); ; day = day.AddDays(1))
        {
            var start = WindowStart(day, hour, zone);
            var end = start.AddHours(1);
            if (earliest < start)
            {
                return start;
            }

            if (earliest < end)
            {
                return earliest;
            }
        }
    }

    /// <summary>True when <paramref name="now"/> is at or after <paramref name="due"/>.</summary>
    public static bool IsDue(DateTimeOffset now, DateTimeOffset due) => now >= due;

    /// <summary>The window's start on a local calendar day; an hour skipped by a clock change starts at the next valid time.</summary>
    private static DateTimeOffset WindowStart(DateTime localDay, int hour, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(localDay.AddHours(hour), DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
