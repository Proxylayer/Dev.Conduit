using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Updates;

/// <summary>
/// The <c>DevConduit:Update</c> settings: where releases come from, how often to look, whether a
/// verified release installs on its own, and the service's quiet hour. An empty
/// <see cref="FeedUrl"/> turns updates off.
/// </summary>
public sealed class UpdateOptions
{
    public const string SectionName = "DevConduit:Update";

    public const string DefaultFeedUrl = "https://github.com/Proxylayer/Dev.Conduit";

    public const string AutoInstallKey = ManagedSettingKeys.UpdateAutoInstall;

    /// <summary>The GitHub repository whose published releases are the feed; empty disables updates.</summary>
    public string FeedUrl { get; set; } = DefaultFeedUrl;

    /// <summary>Hours between checks after the one at startup; 0 or less checks at startup only.</summary>
    public double CheckIntervalHours { get; set; } = 1;

    /// <summary>Whether a verified release is installed without asking.</summary>
    public bool AutoInstall { get; set; } = true;

    /// <summary>The local hour, 0 to 23, at which the service installs a verified release.</summary>
    public int InstallHour { get; set; } = 3;

    /// <summary>Whether a prerelease counts as a release. Drafts never do.</summary>
    public bool IncludePrerelease { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(FeedUrl);

    /// <summary>The interval between checks, or null for a startup check only.</summary>
    public TimeSpan? CheckInterval =>
        CheckIntervalHours > 0 && double.IsFinite(CheckIntervalHours) ? TimeSpan.FromHours(Math.Min(CheckIntervalHours, 24 * 30)) : null;

    /// <summary><see cref="InstallHour"/> kept within 0 to 23.</summary>
    public int EffectiveInstallHour => Math.Clamp(InstallHour, 0, 23);

    /// <summary>
    /// The settings in <paramref name="configuration"/>, each one that is missing or unreadable
    /// left at its default, so a typo never stops updates from being reported.
    /// </summary>
    public static UpdateOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var options = new UpdateOptions();

        if (section[nameof(FeedUrl)] is { } feed)
        {
            options.FeedUrl = feed.Trim();
        }

        if (double.TryParse(section[nameof(CheckIntervalHours)], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var hours))
        {
            options.CheckIntervalHours = hours;
        }

        options.AutoInstall = IsAutoInstall(configuration);

        if (int.TryParse(section[nameof(InstallHour)], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var hour))
        {
            options.InstallHour = hour;
        }

        if (bool.TryParse(section[nameof(IncludePrerelease)]?.Trim(), out var prerelease))
        {
            options.IncludePrerelease = prerelease;
        }

        return options;
    }

    /// <summary>The <c>DevConduit:Update:AutoInstall</c> switch; anything but <c>false</c> leaves it on.</summary>
    public static bool IsAutoInstall(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !bool.TryParse(configuration[AutoInstallKey]?.Trim(), out var enabled) || enabled;
    }
}
