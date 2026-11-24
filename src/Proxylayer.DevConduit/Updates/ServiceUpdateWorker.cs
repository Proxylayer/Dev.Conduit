using System.Runtime.Versioning;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Updates;

/// <summary>
/// Automatic updates on a server: the Windows service, running as LocalSystem from the MSI's
/// Server role, checks the feed at start and every <c>CheckIntervalHours</c>, downloads and
/// verifies a newer release into the restricted machine update folder and, with
/// <c>AutoInstall</c> on, installs it at the next quiet hour (<c>InstallHour</c>, local time):
/// msiexec is started silently and detached, so it survives the package stopping this service.
/// The next start reports what the install came to; a failed one is retried at most once a day.
/// Anything else (a console run, a build the MSI did not install) only logs that updates are off.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ServiceUpdateWorker : BackgroundService
{
    private const string HistoryFileName = "service-history.json";

    /// <summary>The longest single wait, so a changed clock or a resumed machine is noticed within the hour.</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);

    private readonly UpdateOptions _options;
    private readonly Func<bool> _autoInstall;
    private readonly bool _runningAsService;
    private readonly SettingsFiles _settingsFiles;
    private readonly TimeProvider _time;
    private readonly ILogger<ServiceUpdateWorker> _logger;

    public ServiceUpdateWorker(
        UpdateOptions options,
        Func<bool> autoInstall,
        bool runningAsService,
        SettingsFiles settingsFiles,
        TimeProvider time,
        ILogger<ServiceUpdateWorker> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _autoInstall = autoInstall ?? throw new ArgumentNullException(nameof(autoInstall));
        _runningAsService = runningAsService;
        _settingsFiles = settingsFiles ?? throw new ArgumentNullException(nameof(settingsFiles));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Registers the worker for a server-mode run. The machine settings file is read again before
    /// each install decision, so turning Install updates automatically off in the tray holds
    /// without a restart.
    /// </summary>
    public static void Register(WebApplicationBuilder builder, bool runningAsService, SettingsFiles settingsFiles)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = UpdateOptions.From(builder.Configuration);
        var configuration = builder.Configuration;
        builder.Services.AddHostedService(provider => new ServiceUpdateWorker(
            options,
            () => AutoInstallNow(configuration, settingsFiles.MachinePath),
            runningAsService,
            settingsFiles,
            TimeProvider.System,
            provider.GetRequiredService<ILogger<ServiceUpdateWorker>>()));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Out of the host's startup path: nothing here may delay the web host coming up.
        await Task.Yield();

        var build = InstalledBuild.Detect();
        if (!build.IsInstalledBuild)
        {
            _logger.LogInformation("Update checks are disabled (not an installed build): {Reason}.", build.Reason);
            return;
        }

        if (!_runningAsService)
        {
            _logger.LogInformation("Update checks are disabled (not running as the Dev.Conduit service; the service installs updates).");
            return;
        }

        if (!build.Installer.IsServerRole)
        {
            _logger.LogInformation("Update checks are disabled in the service: the MSI's {Role} role is updated from the tray.", build.Installer.Role);
            return;
        }

        var folder = UpdateFolder.Machine();
        var history = ReportPreviousAttempt(folder);

        using var feed = GitHubReleaseFeed.Create(_options, ReleaseVersion.Current, _time, _logger, out var reason);
        if (feed is null)
        {
            _logger.LogInformation("{Reason}", reason);
            ServiceUpdateStatusFile.Write(_settingsFiles, UpdateStatus.Disabled("(off)", reason));
            return;
        }

        var verifier = UpdatePackageVerifier.ForThisProcess();
        var downloader = new UpdateDownloader(feed, folder, verifier, _logger);
        var fetcher = new UpdateFetcher(feed, downloader, ReleaseVersion.Current, _options.FeedUrl, _time);
        _logger.LogInformation(
            "Update checks are on: feed {Feed}, every {Interval}, installing at {Hour:00}:00 when automatic install is on. This is {Version}; the exe is {Signature}.",
            _options.FeedUrl, _options.CheckInterval?.ToString() ?? "start only", _options.EffectiveInstallHour,
            ReleaseVersion.CurrentDisplay, verifier.Running.IsSigned ? $"signed by '{verifier.Running.Subject}'" : "unsigned");

        await RunAsync(fetcher, folder, build, history, stoppingToken);
    }

    private async Task RunAsync(UpdateFetcher fetcher, UpdateFolder folder, InstalledBuild build, UpdateHistory history, CancellationToken stoppingToken)
    {
        var nextCheck = _time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_time.GetUtcNow() >= nextCheck)
            {
                try
                {
                    await fetcher.RefreshAsync(stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(exception, "The update check failed; it is tried again at the next interval.");
                }

                nextCheck = _options.CheckInterval is { } interval ? _time.GetUtcNow() + interval : DateTimeOffset.MaxValue;
            }

            var ready = fetcher.Ready;
            DateTimeOffset? due = null;
            var autoInstall = _autoInstall();
            if (ready is not null && autoInstall)
            {
                due = InstallWindow.Next(_time.GetUtcNow(), _options.EffectiveInstallHour, _time.LocalTimeZone,
                    history.RetryNotBefore(ready.Release.Version));
            }

            var status = fetcher.Status;
            ServiceUpdateStatusFile.Write(_settingsFiles, status with
            {
                NextInstall = due,
                Message = ready is not null && !autoInstall ? $"{ready.Message} Automatic install is off." : status.Message
            });

            if (ready is not null && due is { } dueAt && InstallWindow.IsDue(_time.GetUtcNow(), dueAt))
            {
                history = await InstallAsync(ready, folder, build, history, stoppingToken);
                continue;
            }

            var wake = due is { } d && d < nextCheck ? d : nextCheck;
            var wait = wake - _time.GetUtcNow();
            try
            {
                await Task.Delay(wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaxWait ? MaxWait : wait, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Starts msiexec on the verified package and waits for it. Normally the package stops this
    /// service before it ends, which ends the wait; msiexec carries on. An msiexec that ends while
    /// the service still runs failed before it reached the service.
    /// </summary>
    private async Task<UpdateHistory> InstallAsync(DownloadedUpdate ready, UpdateFolder folder, InstalledBuild build, UpdateHistory history, CancellationToken stoppingToken)
    {
        var version = ready.Release.Version;
        var log = folder.LogPath(version);
        if (!MsiexecCommand.TryBuild(ready.Path!, log, build.Installer, out var command, out var error))
        {
            _logger.LogError("Dev.Conduit {Version} cannot be installed: {Error}", ready.Release.Tag, error);
            return Save(folder, history with { FailedVersion = ReleaseVersion.Format(version), FailedAt = _time.GetUtcNow() });
        }

        var attempt = new UpdateAttempt(ReleaseVersion.Format(version), ReleaseVersion.Format(ReleaseVersion.Current), _time.GetUtcNow(), log);
        history = Save(folder, history with { Attempt = attempt });

        _logger.LogWarning(
            "Installing Dev.Conduit {Version} over {Current} now: {FileName} {Arguments}. The service stops while its files are replaced and starts again afterwards. Log: {Log}",
            ready.Release.Tag, ReleaseVersion.CurrentDisplay, command.FileName, command.Arguments, log);

        System.Diagnostics.Process process;
        try
        {
            process = command.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogError("msiexec could not be started: {Message}", exception.Message);
            return Save(folder, history with { Attempt = null, FailedVersion = attempt.Version, FailedAt = attempt.StartedAt });
        }

        using (process)
        {
            try
            {
                await process.WaitForExitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Stopped by the package: the next start reports the outcome.
                return history;
            }

            if (MsiexecCommand.Succeeded(process.ExitCode))
            {
                _logger.LogWarning("msiexec installed Dev.Conduit {Version} (exit code {ExitCode}) without restarting the service; restart it to run the new version. Log: {Log}",
                    ready.Release.Tag, process.ExitCode, log);
                return Save(folder, history with { Attempt = null });
            }

            _logger.LogError(
                "The update to Dev.Conduit {Version} failed: msiexec exit code {ExitCode}. Dev.Conduit {Current} keeps running; the next attempt is not before {Retry}. Log: {Log}",
                ready.Release.Tag, process.ExitCode, ReleaseVersion.CurrentDisplay, (attempt.StartedAt + InstallWindow.RetryAfterFailure).ToLocalTime(), log);
            return Save(folder, history with { Attempt = null, FailedVersion = attempt.Version, FailedAt = attempt.StartedAt });
        }
    }

    /// <summary>Logs what the install started before this start came to, and clears it.</summary>
    private UpdateHistory ReportPreviousAttempt(UpdateFolder folder)
    {
        var history = UpdateRecordFile.Read<UpdateHistory>(folder.File(HistoryFileName)) ?? UpdateHistory.Empty;
        switch (history.Outcome(ReleaseVersion.Current))
        {
            case UpdateOutcome.Installed:
                _logger.LogWarning("Dev.Conduit was updated from {From} to {Version}.", history.Attempt!.FromVersion, ReleaseVersion.CurrentDisplay);
                return Save(folder, history with { Attempt = null, FailedVersion = null, FailedAt = null });

            case UpdateOutcome.Failed:
                var attempt = history.Attempt!;
                var exitCode = MsiexecCommand.ExitCodeFromLog(attempt.LogPath);
                _logger.LogError(
                    "The update to Dev.Conduit {Version} did not install (msiexec exit code {ExitCode}); Dev.Conduit {Current} keeps running and the next attempt is not before {Retry}. Log: {Log}",
                    attempt.Version, exitCode?.ToString() ?? "unknown", ReleaseVersion.CurrentDisplay,
                    (attempt.StartedAt + InstallWindow.RetryAfterFailure).ToLocalTime(), attempt.LogPath);
                return Save(folder, history with { Attempt = null, FailedVersion = attempt.Version, FailedAt = attempt.StartedAt });

            default:
                return history;
        }
    }

    private UpdateHistory Save(UpdateFolder folder, UpdateHistory history)
    {
        try
        {
            folder.Ensure();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The update folder {Folder} could not be prepared: {Message}", folder.Path, exception.Message);
        }

        if (!UpdateRecordFile.Write(folder.File(HistoryFileName), history))
        {
            _logger.LogWarning("The update history could not be saved in {Folder}.", folder.Path);
        }

        return history;
    }

    /// <summary>
    /// <c>DevConduit:Update:AutoInstall</c> as it stands now: the machine settings file's value when it
    /// has one, else what the service started with.
    /// </summary>
    private static bool AutoInstallNow(IConfiguration startup, string machinePath)
    {
        try
        {
            if (File.Exists(machinePath))
            {
                var machine = new ConfigurationBuilder().AddJsonFile(machinePath, optional: true, reloadOnChange: false).Build();
                if (machine[UpdateOptions.AutoInstallKey] is not null)
                {
                    return UpdateOptions.IsAutoInstall(machine);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or UnauthorizedAccessException)
        {
            // The file is checked at startup; a broken edit since keeps the startup value.
        }

        return UpdateOptions.IsAutoInstall(startup);
    }
}
