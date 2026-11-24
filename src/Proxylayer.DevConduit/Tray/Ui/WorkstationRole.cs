using System.Runtime.Versioning;
using System.Windows.Forms;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tray.Ui;

/// <summary>
/// The tray on a developer workstation: hosts local mode in this process, starts and stops
/// it on request, and rebuilds it on each start so saved settings apply. It also starts it on
/// its own when the tray starts, or after a save leaves it stopped, if the configuration is
/// ready (<see cref="LocalProxyAutoStart"/>) and Start proxy automatically is on. Exiting the
/// tray stops it cleanly. When an imported server's new certificate is pinned but not yet in
/// Windows trust, or this computer's client certificate for it is due for renewal, it says so
/// once and offers to update both, which needs an administrator.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WorkstationRole : ITrayRole
{
    private const string UpdateCertificatesText = "Update Dev.Conduit certificates (needs admin)";

    private readonly TrayServices _services;
    private readonly LocalProxyHost _host;
    private readonly StartWithWindows _startup = StartWithWindows.ForCurrentUser();
    private readonly StartWithWindows _machineStartup = StartWithWindows.ForAllUsers();
    private readonly SynchronizationContext _ui;
    private TrayStatus _status;
    private long _startedAfterSequence;
    private bool _trustNoticeShown;

    public WorkstationRole(TrayServices services)
    {
        _services = services;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _host = new LocalProxyHost(services.SettingsFiles, services.LogProvider);
        _host.StateChanged += (_, _) => _ui.Post(_ => OnStateChanged(), null);
        _status = Describe();
        RemoveRedundantUserStartup();

        // Once the message loop runs: a workstation whose servers are imported needs no click.
        _ui.Post(_ => TrayMenu.Run(() => AutoStartAsync("the tray started"), "Starting the local proxy", _services), null);
    }

    public event EventHandler? StatusChanged;

    public TrayRole Role => TrayRole.Workstation;

    public TrayStatus Status => _status;

    public IReadOnlyList<ClientTrustStatus> TrustStatuses => _host.TrustStatuses;

    public IReadOnlyList<ToolStripItem> CreateMenuItems()
    {
        var state = _host.State;
        var running = state == LocalProxyState.Running;
        var local = _host.RunningOptions ?? ConfiguredOptions();

        // The installer's entry for every user wins: shown on, and changed only by uninstalling.
        var machineWide = _machineStartup.IsEnabled;
        var startWithWindows = new ToolStripMenuItem("Start with Windows")
        {
            Checked = machineWide || _startup.IsEnabled,
            CheckOnClick = false,
            Enabled = !machineWide
        };
        if (machineWide)
        {
            startWithWindows.ToolTipText = "Set for every user by the installer.";
        }
        else if (_startup.PointsElsewhere)
        {
            startWithWindows.ToolTipText = $"Starts another copy. Click twice to point it at {Environment.ProcessPath}.";
        }

        startWithWindows.Click += (_, _) => ToggleStartWithWindows();

        var autoStart = new ToolStripMenuItem("Start proxy automatically") { Checked = AutoStartEnabled(), CheckOnClick = false };
        autoStart.Click += (_, _) => TrayMenu.Run(ToggleAutoStartAsync, "Start proxy automatically", _services);

        List<ToolStripItem> items =
        [
            state is LocalProxyState.Starting or LocalProxyState.Running
                ? TrayMenu.Item("Stop local proxy", _host.StopAsync, _services)
                : TrayMenu.Item("Start local proxy", StartAsync, _services),
            TrayMenu.Item("Restart local proxy", RestartAsync, _services, enabled: running),
            new ToolStripSeparator(),
            TrayMenu.Item("Open local console", () => TrayMenu.OpenInBrowser(ConsoleAddresses.LocalConsole(local), _services), enabled: running),
            TrayMenu.Item(
                "Copy proxy address",
                () => Clipboard.SetText(ConsoleAddresses.ForwardProxy(local)),
                enabled: local.ForwardProxyEnabled),
            TrayMenu.Item("View log...", _services.OpenLog),
            new ToolStripSeparator(),
            startWithWindows,
            autoStart
        ];

        if (TrustUpdates().Count > 0)
        {
            items.InsertRange(0, [TrayMenu.Item(UpdateCertificatesText + "...", TrustAsync, _services), new ToolStripSeparator()]);
        }

        // Only where the tray may install the service at all: never while the MSI is installed.
        if (ServiceActions.For(status: null, InstallerOwnership.Detect()).CanInstall)
        {
            items.AddRange([new ToolStripSeparator(), TrayMenu.Item("Install service...", InstallServiceAsync, _services)]);
        }

        return items;
    }

    public void Poll()
    {
        // A running proxy that has logged an error since it started turns amber.
        SetStatus(Describe());
        NoticeTrustUpdates();
    }

    public async Task SettingsSavedAsync(SettingsTarget target, IWin32Window? owner)
    {
        if (_host.State != LocalProxyState.Running)
        {
            // An import, or a Routes or Local proxy save, may be what makes the configuration
            // complete: a stopped proxy then starts as it would with the tray.
            await AutoStartAsync("the settings changed");
            return;
        }

        var answer = MessageBox.Show(
            owner,
            "The local proxy reads its settings when it starts. Restart it now so the changes take effect?",
            "Dev.Conduit",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (answer == DialogResult.Yes)
        {
            await RestartAsync();
        }
    }

    public Task ShutdownAsync() => _host.StopAsync();

    // Run off the UI thread so nothing the host awaits can wait on the message loop.
    public void Dispose() => Task.Run(() => _host.DisposeAsync().AsTask()).GetAwaiter().GetResult();

    private Task StartAsync()
    {
        _startedAfterSequence = _services.Log.LastSequence;
        return _host.StartAsync();
    }

    private Task RestartAsync()
    {
        _startedAfterSequence = _services.Log.LastSequence;
        return _host.RestartAsync();
    }

    private void OnStateChanged()
    {
        SetStatus(Describe());

        if (_host.State != LocalProxyState.Failed || _host.Failure is not { } failure)
        {
            return;
        }

        if (_host.FailedOnConfiguration)
        {
            _services.Notify(
                "Local proxy: configuration needs attention",
                $"{FirstProblem(failure)} Click to open Settings.",
                ToolTipIcon.Error,
                _services.OpenSettings);
        }
        else
        {
            _services.Notify("Local proxy could not start", $"{Shorten(failure)} Click to view the log.", ToolTipIcon.Error, _services.OpenLog);
        }
    }

    /// <summary>
    /// The imported servers <c>service-admin trust</c> has something to do for
    /// (<see cref="ClientTrustStatus.UpdateNeeded"/>): a pinned certificate Windows does not trust
    /// yet, or a machine identity due for renewal.
    /// </summary>
    private IReadOnlyList<string> TrustUpdates() =>
        _host.TrustStatuses.Where(status => status.UpdateNeeded).Select(status => status.Hostname).ToList();

    /// <summary>Says once that a certificate update waits for an administrator; again only after that was settled.</summary>
    private void NoticeTrustUpdates()
    {
        var due = _host.TrustStatuses.Where(status => status.UpdateNeeded).ToList();
        if (due.Count == 0)
        {
            _trustNoticeShown = false;
            return;
        }

        if (_trustNoticeShown)
        {
            return;
        }

        _trustNoticeShown = true;
        var reasons = new List<string>();
        if (due.Where(status => status.RootStoreUpdateNeeded).Select(status => status.Hostname).ToList() is { Count: > 0 } untrusted)
        {
            reasons.Add($"{string.Join(", ", untrusted)}: Windows does not trust the new server certificate yet.");
        }

        if (due.Where(status => status.MachineIdentityRenewalDue).Select(status => status.Hostname).ToList() is { Count: > 0 } renewals)
        {
            reasons.Add($"{string.Join(", ", renewals)}: this computer's client certificate is due for renewal.");
        }

        _services.Notify(
            UpdateCertificatesText,
            $"{string.Join(" ", reasons)} The local proxy works meanwhile. Click to update (needs an administrator).",
            ToolTipIcon.Info,
            () => TrayMenu.Run(TrustAsync, "Updating Dev.Conduit certificates", _services));
    }

    /// <summary>
    /// Runs <c>service-admin trust</c> for each server that needs it, through the elevated helper:
    /// Windows trust brought up to date and the machine identity renewed when due. Then syncs again.
    /// </summary>
    private async Task TrustAsync()
    {
        foreach (var hostname in TrustUpdates())
        {
            var outcome = await _services.Admin.RunAsync(ServiceAdminCommand.TrustAction, hostname);
            if (outcome.Cancelled)
            {
                return;
            }

            if (outcome.Succeeded)
            {
                _services.Logger.LogInformation("The Dev.Conduit certificates of {Host} are up to date.", hostname);
            }
            else
            {
                _services.Logger.LogError("The Dev.Conduit certificates of {Host} could not be updated: {Message}", hostname, outcome.Message);
                _services.Notify("Certificate update failed", outcome.Message, ToolTipIcon.Error, _services.OpenLog);
            }
        }

        _host.RequestCertificateSync();
    }

    private async Task InstallServiceAsync()
    {
        var answer = MessageBox.Show(
            "Install the Dev.Conduit Windows service on this computer? The tray then manages the service instead of running the local proxy.",
            "Dev.Conduit",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        var outcome = await _services.Admin.RunAsync(ServiceAdminCommand.InstallAction, Environments.Production);
        if (outcome.Cancelled)
        {
            return;
        }

        if (!outcome.Succeeded)
        {
            _services.Notify("The service was not installed", outcome.Message, ToolTipIcon.Error, null);
            return;
        }

        _services.Logger.LogInformation("The service was installed.");
        var start = await _services.Admin.RunAsync(ServiceAdminCommand.StartAction);
        if (!start.Succeeded && !start.Cancelled)
        {
            _services.Notify("The service was installed but did not start", start.Message, ToolTipIcon.Warning, EventLogViewer.Open);
        }
    }

    private TrayStatus Describe() => _host.State switch
    {
        LocalProxyState.Starting => new TrayStatus(TrayStatusKind.Busy, "Local proxy starting (signing in)"),
        LocalProxyState.Running when _services.Log.HasErrorAfter(_startedAfterSequence) =>
            new TrayStatus(TrayStatusKind.Attention, "Local proxy running, with errors (see log)"),
        LocalProxyState.Running => new TrayStatus(TrayStatusKind.Running, "Local proxy running"),
        LocalProxyState.Failed => new TrayStatus(TrayStatusKind.Failed, "Local proxy failed"),
        _ => new TrayStatus(TrayStatusKind.Stopped, "Local proxy stopped")
    };

    private void SetStatus(TrayStatus status)
    {
        if (status == _status)
        {
            return;
        }

        _status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The local settings a start would use, for the addresses shown while stopped.</summary>
    private LocalModeOptions ConfiguredOptions()
    {
        try
        {
            return UserView().Effective.GetSection($"{DevConduitOptions.SectionName}:Local").Get<LocalModeOptions>()
                ?? new LocalModeOptions();
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            return new LocalModeOptions();
        }
    }

    /// <summary>The user settings file over every layer below it: the files a start reads.</summary>
    private SettingsView UserView() =>
        new SettingsLayers(
                DevConduitApplication.ResolveContentRoot(runningAsService: false) ?? Directory.GetCurrentDirectory(),
                SettingsLayers.ProcessEnvironmentName(),
                _services.SettingsFiles)
            .View(SettingsTarget.User);

    /// <summary>
    /// Starts a stopped (or failed) local proxy when <see cref="LocalProxyAutoStart"/> says the
    /// configuration is ready. Called once when the tray starts and once per save, never in a
    /// loop: a start that fails shows the failed status and one notification, and waits.
    /// </summary>
    private async Task AutoStartAsync(string why)
    {
        if (_host.State is not (LocalProxyState.Stopped or LocalProxyState.Failed))
        {
            return;
        }

        LocalProxyAutoStartDecision decision;
        try
        {
            decision = LocalProxyAutoStart.Decide(UserView().Effective);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            _services.Logger.LogWarning("The local proxy was not started automatically: {Message}", exception.Message);
            return;
        }

        if (!decision.Start)
        {
            _services.Logger.LogInformation("The local proxy was not started automatically when {Why}: {Reason}", why, decision.Reason);
            return;
        }

        _services.Logger.LogInformation("Starting the local proxy automatically: {Why}. {Reason}", why, decision.Reason);
        await StartAsync();
    }

    private bool AutoStartEnabled()
    {
        try
        {
            return LocalProxyAutoStart.IsEnabled(UserView().Effective);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>Turns Start proxy automatically on or off in the user settings file; on is the default and is not written.</summary>
    private async Task ToggleAutoStartAsync()
    {
        var view = UserView();
        var changes = new SettingsChangeSet();
        SettingValues.Switch(
            changes,
            LocalProxyAutoStart.Key,
            !LocalProxyAutoStart.IsEnabled(view.Effective),
            LocalProxyAutoStart.IsEnabled(view.Below));

        var outcome = await new SettingsSaver(_services.SettingsFiles, _services.Admin).SaveAsync(SettingsTarget.User, changes);
        if (!outcome.Succeeded)
        {
            _services.Notify("Start proxy automatically", outcome.Message, ToolTipIcon.Warning, null);
        }
    }

    private void ToggleStartWithWindows()
    {
        try
        {
            if (_startup.IsEnabled && !_startup.PointsElsewhere)
            {
                _startup.Disable();
            }
            else
            {
                _startup.Enable();
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _services.Notify("Start with Windows", exception.Message, ToolTipIcon.Warning, null);
        }
    }

    /// <summary>
    /// Drops the current user's entry when the installer's entry for every user starts the same
    /// exe. Both would start the tray at sign-in, and the second launch would open the setup
    /// window of the first.
    /// </summary>
    private void RemoveRedundantUserStartup()
    {
        try
        {
            if (_machineStartup.IsEnabled && !_machineStartup.PointsElsewhere && _startup.IsEnabled && !_startup.PointsElsewhere)
            {
                _startup.Disable();
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Left in place: the second launch at sign-in only opens the setup window.
        }
    }

    /// <summary>The first problem of a fatal startup message, whose problems are listed as " - problem".</summary>
    private static string FirstProblem(string message)
    {
        var problem = message.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("- ", StringComparison.Ordinal));
        return Shorten(problem is null ? message : problem[2..]);
    }

    private static string Shorten(string text)
    {
        var single = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return single.Length <= 180 ? single : single[..177] + "...";
    }
}
