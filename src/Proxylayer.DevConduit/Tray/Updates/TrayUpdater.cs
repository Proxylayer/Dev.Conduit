using System.Runtime.Versioning;
using System.Windows.Forms;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tray.Setup;
using Proxylayer.DevConduit.Tray.Ui;
using Proxylayer.DevConduit.Updates;

namespace Proxylayer.DevConduit.Tray.Updates;

/// <summary>
/// Updates as the tray sees them. On a workstation (the MSI's Workstation role) it checks the feed
/// shortly after starting and every <c>CheckIntervalHours</c>, downloads and verifies a newer
/// release in the background into <c>%LOCALAPPDATA%\Proxylayer\Dev.Conduit\updates</c>, and with
/// Install updates automatically on hands it to the elevated helper, which shows one UAC prompt;
/// the tray then exits and comes back when msiexec ends. A declined prompt turns that version into
/// an offer until the tray next starts: a menu item and a balloon, never another prompt. On a
/// server the service installs updates; the tray shows what the service reported and checks only
/// when asked. A build the MSI did not install never checks. Everything runs on the UI thread
/// except the check and download themselves.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TrayUpdater : IDisposable
{
    private const string HistoryFileName = "tray-history.json";
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);

    private readonly TrayServices _services;
    private readonly Func<Task> _exitTray;
    private readonly InstalledBuild _build;
    private readonly UpdateOptions _options;
    private readonly UpdateFolder _folder = UpdateFolder.User();
    private readonly UpdateFetcher? _fetcher;
    private readonly UpdateHandoff _handoff;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly HashSet<Version> _declined = [];
    private readonly string _disabledReason = string.Empty;
    private readonly CancellationTokenSource _stopping = new();
    private UpdateHistory _history;
    private bool _busy;

    public TrayUpdater(TrayServices services, Func<Task> exitTray)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _exitTray = exitTray ?? throw new ArgumentNullException(nameof(exitTray));
        _build = InstalledBuild.Detect();
        _handoff = UpdateHandoff.ForThisProcess(services.Admin, services.Logger);
        _options = ReadOptions();
        _history = UpdateRecordFile.Read<UpdateHistory>(_folder.File(HistoryFileName)) ?? UpdateHistory.Empty;

        if (!_build.IsInstalledBuild)
        {
            _disabledReason = $"Update checks are disabled (not an installed build): {_build.Reason}.";
            return;
        }

        var feed = GitHubReleaseFeed.Create(_options, ReleaseVersion.Current, TimeProvider.System, services.Logger, out var reason);
        if (feed is null)
        {
            _disabledReason = reason;
            return;
        }

        var downloader = InstallsUpdates ? new UpdateDownloader(feed, _folder, UpdatePackageVerifier.ForThisProcess(), services.Logger) : null;
        _fetcher = new UpdateFetcher(feed, downloader, ReleaseVersion.Current, _options.FeedUrl, TimeProvider.System);
    }

    /// <summary>Raised on the UI thread when the status changes.</summary>
    public event EventHandler? Changed;

    /// <summary>True when this is an installed build and a feed is configured.</summary>
    public bool IsEnabled => _fetcher is not null;

    /// <summary>True on a workstation: this tray downloads and installs updates. On a server the service does.</summary>
    public bool InstallsUpdates => _build.IsInstalledBuild && !_build.Installer.IsServerRole;

    public bool IsServer => _build.Installer.IsServerRole;

    /// <summary>
    /// What to show: on a server, the service's own report when it has written one; otherwise this
    /// tray's.
    /// </summary>
    public UpdateStatus Status =>
        (IsServer ? ServiceUpdateStatusFile.Read(_services.SettingsFiles) : null)
        ?? _fetcher?.Status
        ?? UpdateStatus.Disabled(_options.Enabled ? _options.FeedUrl : "(off)", _disabledReason);

    /// <summary>Install updates automatically, as the settings file this role writes it to says now.</summary>
    public bool AutoInstall
    {
        get
        {
            try
            {
                return UpdateOptions.IsAutoInstall(SettingsView().Effective);
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
            {
                return true;
            }
        }
    }

    /// <summary>Reports how the last install went, then checks shortly and every interval after (workstation only).</summary>
    public void Start()
    {
        if (!IsEnabled)
        {
            _services.Logger.LogInformation("{Reason}", _disabledReason);
            return;
        }

        ReportPreviousAttempt();
        if (!InstallsUpdates)
        {
            return;
        }

        _timer.Interval = (int)FirstCheckDelay.TotalMilliseconds;
        _timer.Tick += (_, _) =>
        {
            if (_options.CheckInterval is { } interval)
            {
                _timer.Interval = (int)Math.Min(interval.TotalMilliseconds, int.MaxValue);
            }
            else
            {
                _timer.Stop();
            }

            TrayMenu.Run(() => CheckAsync(userAsked: false), "Checking for updates", _services);
        };
        _timer.Start();
    }

    public IReadOnlyList<ToolStripItem> CreateMenuItems()
    {
        var check = TrayMenu.Item("Check for updates", () => CheckAsync(userAsked: true), _services, enabled: IsEnabled && !_busy);
        if (!IsEnabled)
        {
            check.ToolTipText = _disabledReason;
            return [check];
        }

        List<ToolStripItem> items = [check];
        if (InstallsUpdates && _fetcher?.Ready is { } ready)
        {
            items.Add(TrayMenu.Item($"Install update {ready.Release.Tag} (needs admin)...", InstallAsync, _services, enabled: !_busy));
        }

        var auto = new ToolStripMenuItem("Install updates automatically") { Checked = AutoInstall, CheckOnClick = false };
        auto.ToolTipText = IsServer
            ? $"The service installs a verified release at {_options.EffectiveInstallHour:00}:00. Changing this needs an administrator."
            : "Downloads, verifies and installs a new release; installing asks for an administrator once.";
        auto.Click += (_, _) => TrayMenu.Run(() => SetAutoInstallAsync(!AutoInstall), "Install updates automatically", _services);
        items.Add(auto);
        return items;
    }

    /// <summary>Asks the feed; on a workstation also downloads, verifies, and installs or offers.</summary>
    public async Task CheckAsync(bool userAsked)
    {
        if (_fetcher is null || _busy)
        {
            return;
        }

        _busy = true;
        UpdateStatus status;
        try
        {
            status = await Task.Run(() => _fetcher.RefreshAsync(_stopping.Token));
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            _busy = false;
        }

        OnChanged();

        if (!InstallsUpdates)
        {
            if (userAsked)
            {
                _services.Notify("Dev.Conduit updates", ServerMessage(status), ToolTipIcon.Info, _services.OpenSettings);
            }

            return;
        }

        if (userAsked && _fetcher.Ready is null)
        {
            _services.Notify("Dev.Conduit updates", status.Message, ToolTipIcon.Info, null);
        }

        await ActOnReadyAsync();
    }

    /// <summary>Installs the ready package through the elevated helper (workstation only).</summary>
    public async Task InstallAsync()
    {
        if (!InstallsUpdates || _fetcher?.Ready is not { } ready || _busy || _build.ExePath is not { } exe)
        {
            return;
        }

        _busy = true;
        try
        {
            var version = ready.Release.Version;
            var log = UpdateFolder.Machine().LogPath(version);
            SaveHistory(_history with
            {
                Attempt = new UpdateAttempt(ReleaseVersion.Format(version), ReleaseVersion.Format(ReleaseVersion.Current), DateTimeOffset.UtcNow, log)
            });

            var outcome = await Task.Run(() => _handoff.Run(ready.Path!, exe, Environment.ProcessId));
            switch (outcome.Result)
            {
                case HandoffResult.Started:
                    _services.Logger.LogInformation(
                        "Installing Dev.Conduit {Tag}: the tray exits now and starts again when the install ends. Log: {Log}", ready.Release.Tag, log);
                    await _exitTray();
                    return;

                case HandoffResult.Declined:
                    _declined.Add(version);
                    SaveHistory(_history with { Attempt = null });
                    _services.Logger.LogInformation("The administrator prompt for Dev.Conduit {Tag} was declined; it waits for a click until the tray restarts.", ready.Release.Tag);
                    Offer(ready, force: true);
                    break;

                default:
                    // Held back a day like a failed install, so an automatic retry can never loop.
                    SaveHistory(_history with { Attempt = null, FailedVersion = ReleaseVersion.Format(version), FailedAt = DateTimeOffset.UtcNow });
                    _services.Logger.LogError("Dev.Conduit {Tag} could not be installed: {Message}", ready.Release.Tag, outcome.Message);
                    _services.Notify("Dev.Conduit update failed", outcome.Message, ToolTipIcon.Error, _services.OpenLog);
                    break;
            }
        }
        finally
        {
            _busy = false;
            OnChanged();
        }
    }

    /// <summary>
    /// Turns Install updates automatically on or off: in the user settings file on a workstation,
    /// in the machine settings file on a server (through the elevated helper). On is the default
    /// and is not written.
    /// </summary>
    public async Task SetAutoInstallAsync(bool enabled)
    {
        var view = SettingsView();
        var changes = new SettingsChangeSet();
        SettingValues.Switch(changes, UpdateOptions.AutoInstallKey, enabled, UpdateOptions.IsAutoInstall(view.Below));

        var outcome = await new SettingsSaver(_services.SettingsFiles, _services.Admin)
            .SaveAsync(IsServer ? SettingsTarget.Machine : SettingsTarget.User, changes);
        if (!outcome.Succeeded && !outcome.Cancelled)
        {
            _services.Notify("Install updates automatically", outcome.Message, ToolTipIcon.Warning, null);
        }

        OnChanged();
        if (outcome.Succeeded && enabled)
        {
            await ActOnReadyAsync();
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _timer.Dispose();
        _fetcher?.Dispose();
        _stopping.Dispose();
    }

    private async Task ActOnReadyAsync()
    {
        if (_fetcher?.Ready is not { } ready)
        {
            return;
        }

        var version = ready.Release.Version;
        var action = TrayUpdateDecision.Decide(
            InstallsUpdates, packageReady: true, AutoInstall, _declined.Contains(version), _history.RetryNotBefore(version), DateTimeOffset.UtcNow);

        if (action == TrayUpdateAction.Install)
        {
            await InstallAsync();
        }
        else if (action == TrayUpdateAction.Offer)
        {
            Offer(ready, force: false);
        }
    }

    /// <summary>One balloon per version (<paramref name="force"/> after a declined prompt); clicking it installs.</summary>
    private void Offer(DownloadedUpdate ready, bool force)
    {
        var version = ReleaseVersion.Format(ready.Release.Version);
        if (!force && string.Equals(_history.NotifiedVersion, version, StringComparison.Ordinal))
        {
            return;
        }

        SaveHistory(_history with { NotifiedVersion = version });
        _services.Notify(
            $"Dev.Conduit {ready.Release.Tag} is ready to install",
            "It is downloaded and verified. Click to install it now (needs an administrator), or use the tray menu later.",
            ToolTipIcon.Info,
            () => TrayMenu.Run(InstallAsync, "Installing the update", _services));
    }

    /// <summary>Says how the install this tray handed over before it restarted came out, once.</summary>
    private void ReportPreviousAttempt()
    {
        var attempt = _history.Attempt;
        switch (_history.Outcome(ReleaseVersion.Current))
        {
            case UpdateOutcome.Installed:
                _services.Logger.LogInformation("Dev.Conduit was updated from {From} to {Version}.", attempt!.FromVersion, ReleaseVersion.CurrentDisplay);
                _services.Notify("Dev.Conduit was updated", $"Dev.Conduit {ReleaseVersion.CurrentDisplay} is running.", ToolTipIcon.Info, null);
                SaveHistory(_history with { Attempt = null, FailedVersion = null, FailedAt = null });
                break;

            case UpdateOutcome.Failed:
                var helperLog = UpdateFolder.Machine().File("update-helper.log");
                var message = $"The update to {attempt!.Version} did not install; {ReleaseVersion.CurrentDisplay} keeps running and installs it automatically "
                    + $"again no sooner than a day from now. Administrators can read why in {attempt.LogPath} or {helperLog}.";
                _services.Logger.LogWarning("{Message}", message);
                _services.Notify("Dev.Conduit update did not install", message, ToolTipIcon.Warning, _services.OpenLog);
                SaveHistory(_history with { Attempt = null, FailedVersion = attempt.Version, FailedAt = attempt.StartedAt });
                break;
        }
    }

    private string ServerMessage(UpdateStatus status)
    {
        if (status.Available is not { } available)
        {
            return status.Message;
        }

        return AutoInstall
            ? $"Dev.Conduit {available} is available. The service downloads, verifies and installs it at {_options.EffectiveInstallHour:00}:00."
            : $"Dev.Conduit {available} is available. Install updates automatically is off: install it with the MSI.";
    }

    private void SaveHistory(UpdateHistory history)
    {
        _history = history;
        try
        {
            _folder.Ensure();
            UpdateRecordFile.Write(_folder.File(HistoryFileName), history);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _services.Logger.LogWarning("The update history could not be saved in {Folder}: {Message}", _folder.Path, exception.Message);
        }
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private UpdateOptions ReadOptions()
    {
        try
        {
            return UpdateOptions.From(SettingsView().Effective);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            _services.Logger.LogWarning("The update settings could not be read; the defaults apply: {Message}", exception.Message);
            return new UpdateOptions();
        }
    }

    /// <summary>The settings file this role's switch lives in, over the layers below it.</summary>
    private SettingsView SettingsView() =>
        IsServer
            ? new SettingsLayers(AppContext.BaseDirectory, SettingsLayers.ServiceEnvironmentName(), _services.SettingsFiles).View(SettingsTarget.Machine)
            : new SettingsLayers(
                    DevConduitApplication.ResolveContentRoot(runningAsService: false) ?? Directory.GetCurrentDirectory(),
                    SettingsLayers.ProcessEnvironmentName(),
                    _services.SettingsFiles)
                .View(SettingsTarget.User);
}
