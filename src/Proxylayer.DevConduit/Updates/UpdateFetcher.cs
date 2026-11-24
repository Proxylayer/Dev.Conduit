using System.Runtime.Versioning;

namespace Proxylayer.DevConduit.Updates;

/// <summary>
/// What the updater knows, for the tray's menu and About page and the service's status file.
/// </summary>
/// <param name="Current">This build's version.</param>
/// <param name="Feed">The feed address, or a note that updates are off.</param>
/// <param name="LastCheck">When the feed was last asked, or null when it has not been.</param>
/// <param name="Available">The newer release found, as <c>X.Y.Z</c>, or null.</param>
/// <param name="Ready">True when that release is downloaded and verified.</param>
/// <param name="Message">The latest outcome in a sentence.</param>
/// <param name="NextInstall">When the service will install the ready release, or null.</param>
public sealed record UpdateStatus(
    string Current,
    string Feed,
    DateTimeOffset? LastCheck,
    string? Available,
    bool Ready,
    string Message,
    DateTimeOffset? NextInstall = null)
{
    public static UpdateStatus Disabled(string feed, string reason) =>
        new(ReleaseVersion.CurrentDisplay, feed, null, null, false, reason);
}

/// <summary>
/// One feed and, where this process installs updates, its downloader: asks the feed, downloads
/// and verifies a newer release once, and remembers the outcome. A failed or rate-limited look
/// keeps what the last good one found. Not thread-safe; each caller runs one look at a time.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdateFetcher : IDisposable
{
    private readonly GitHubReleaseFeed _feed;
    private readonly UpdateDownloader? _downloader;
    private readonly Version _current;
    private readonly string _feedDisplay;
    private readonly TimeProvider _time;

    private DateTimeOffset? _lastCheck;
    private UpdateRelease? _available;
    private DownloadedUpdate? _ready;
    private string _message = "Not checked yet.";

    /// <param name="downloader">Null where this process only reports releases (the tray on a server).</param>
    public UpdateFetcher(GitHubReleaseFeed feed, UpdateDownloader? downloader, Version current, string feedDisplay, TimeProvider time)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _downloader = downloader;
        _current = current ?? throw new ArgumentNullException(nameof(current));
        _feedDisplay = feedDisplay ?? throw new ArgumentNullException(nameof(feedDisplay));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>The verified package of <see cref="Available"/>, or null.</summary>
    public DownloadedUpdate? Ready => _ready is { IsReady: true } ready && File.Exists(ready.Path) ? ready : null;

    public UpdateRelease? Available => _available;

    public DateTimeOffset? LastCheck => _lastCheck;

    public UpdateStatus Status => new(
        ReleaseVersion.CurrentDisplay,
        _feedDisplay,
        _lastCheck,
        _available is null ? null : ReleaseVersion.Format(_available.Version),
        Ready is not null,
        _message);

    /// <summary>Asks the feed, and downloads and verifies a newer release not yet downloaded.</summary>
    public async Task<UpdateStatus> RefreshAsync(CancellationToken cancellationToken)
    {
        var check = await _feed.CheckAsync(cancellationToken).ConfigureAwait(false);
        _lastCheck = _time.GetUtcNow();
        _message = check.Message;

        switch (check.Status)
        {
            case FeedCheckStatus.UpdateAvailable when check.Release is { } release:
                if (_available?.Version != release.Version)
                {
                    _ready = null;
                }

                _available = release;
                if (_downloader is not null && Ready is null)
                {
                    _ready = await _downloader.DownloadAsync(release, _current, cancellationToken).ConfigureAwait(false);
                    _message = _ready.Message;
                }
                else if (Ready is not null)
                {
                    _message = Ready.Message;
                }

                break;

            case FeedCheckStatus.UpToDate:
            case FeedCheckStatus.NoRelease:
                _available = null;
                _ready = null;
                break;
        }

        return Status;
    }

    public void Dispose() => _feed.Dispose();
}
