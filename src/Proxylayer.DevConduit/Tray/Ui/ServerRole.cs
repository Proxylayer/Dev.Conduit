using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Windows.Forms;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tray.Setup;
using Proxylayer.DevConduit.Tray.Ui.Setup;

namespace Proxylayer.DevConduit.Tray.Ui;

/// <summary>
/// The tray on the server: shows the installed service's state, polled every few seconds,
/// and starts, stops and restarts it through the elevated helper; exports a client's
/// <c>.conduit</c>; and uninstalls a service the MSI did not install. Exiting the tray
/// leaves the service as it is.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ServerRole : ITrayRole
{
    private readonly TrayServices _services;
    private TrayStatus _status = new(TrayStatusKind.Stopped, "Service state unknown");
    private ServiceControllerStatus? _serviceStatus;
    private string? _lastFailure;
    private bool _busy;

    public ServerRole(TrayServices services)
    {
        _services = services;
        Poll();
    }

    public event EventHandler? StatusChanged;

    public TrayRole Role => TrayRole.Server;

    public TrayStatus Status => _status;

    public IReadOnlyList<ClientTrustStatus> TrustStatuses => [];

    public IReadOnlyList<ToolStripItem> CreateMenuItems()
    {
        var running = _serviceStatus == ServiceControllerStatus.Running;
        var stopped = _serviceStatus == ServiceControllerStatus.Stopped;

        // While the MSI installed the service in its Server role, removing it is the MSI's.
        var actions = ServiceActions.For(_serviceStatus, InstallerOwnership.Detect());
        var uninstall = TrayMenu.Item("Uninstall service...", UninstallAsync, _services, enabled: actions.CanUninstall && !_busy);
        uninstall.ToolTipText = actions.InstallerNote ?? string.Empty;

        return
        [
            TrayMenu.Item("Start service", () => ControlAsync(ServiceAdminCommand.StartAction, "start"), _services, enabled: stopped && !_busy),
            TrayMenu.Item("Stop service", () => ControlAsync(ServiceAdminCommand.StopAction, "stop"), _services, enabled: running && !_busy),
            TrayMenu.Item("Restart service", () => ControlAsync(ServiceAdminCommand.RestartAction, "restart"), _services, enabled: running && !_busy),
            new ToolStripSeparator(),
            TrayMenu.Item("Open console", OpenConsole),
            TrayMenu.Item("Open Event Log", EventLogViewer.Open),
            new ToolStripSeparator(),
            TrayMenu.Item("Export client configuration...", ExportAsync, _services),
            uninstall
        ];
    }

    public void Poll()
    {
        _serviceStatus = ServiceProbe.Status();

        if (_serviceStatus == ServiceControllerStatus.Running)
        {
            _lastFailure = null;
        }

        SetStatus(_serviceStatus switch
        {
            null => new TrayStatus(TrayStatusKind.Stopped, "Service not installed"),
            ServiceControllerStatus.Running => new TrayStatus(TrayStatusKind.Running, "Service running"),
            ServiceControllerStatus.StartPending => new TrayStatus(TrayStatusKind.Busy, "Service starting"),
            ServiceControllerStatus.StopPending => new TrayStatus(TrayStatusKind.Busy, "Service stopping"),
            ServiceControllerStatus.ContinuePending => new TrayStatus(TrayStatusKind.Busy, "Service resuming"),
            ServiceControllerStatus.PausePending or ServiceControllerStatus.Paused => new TrayStatus(TrayStatusKind.Attention, "Service paused"),
            _ when _lastFailure is not null => new TrayStatus(TrayStatusKind.Failed, "Service failed to start"),
            _ => new TrayStatus(TrayStatusKind.Stopped, "Service stopped")
        });
    }

    public async Task SettingsSavedAsync(SettingsTarget target, IWin32Window? owner)
    {
        if (target != SettingsTarget.Machine || ServiceProbe.Status() != ServiceControllerStatus.Running)
        {
            return;
        }

        var answer = MessageBox.Show(
            owner,
            "The service reads its settings when it starts. Restart it now so the changes take effect?",
            "Dev.Conduit",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (answer == DialogResult.Yes)
        {
            await ControlAsync(ServiceAdminCommand.RestartAction, "restart");
        }
    }

    public Task ShutdownAsync() => Task.CompletedTask;

    public void Dispose()
    {
    }

    private async Task ControlAsync(string action, string verb)
    {
        _busy = true;
        try
        {
            var outcome = await _services.Admin.RunAsync(action);
            if (outcome.Cancelled)
            {
                return;
            }

            if (outcome.Succeeded)
            {
                _services.Logger.LogInformation("The service was asked to {Action} and did.", verb);
            }
            else
            {
                _lastFailure = outcome.Message;
                _services.Logger.LogError("The service could not {Action}: {Message}", verb, outcome.Message);
                _services.Notify($"Could not {verb} the service", outcome.Message, ToolTipIcon.Error, EventLogViewer.Open);
            }
        }
        finally
        {
            _busy = false;
            Poll();
        }
    }

    private Task ExportAsync() =>
        ClientConfigExportFlow.RunAsync(owner: null, _services.Admin, MachineConfiguration());

    private async Task UninstallAsync()
    {
        var answer = MessageBox.Show(
            "Stop and uninstall the Dev.Conduit service? Its settings and data stay where they are.",
            "Dev.Conduit",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer == DialogResult.Yes)
        {
            await ControlAsync(ServiceAdminCommand.UninstallAction, "uninstall");
        }
    }

    /// <summary>
    /// What the service reads: the exe's appsettings files and the machine settings file, with
    /// the computer's name as the host name when they name none, as the service itself applies it.
    /// </summary>
    private IConfiguration MachineConfiguration()
    {
        var effective = new SettingsLayers(AppContext.BaseDirectory, SettingsLayers.ServiceEnvironmentName(), _services.SettingsFiles)
            .View(SettingsTarget.Machine).Effective;
        return ServerHostnameDefault.With(effective, ServerHostnameDefault.For(effective, applies: true, ServerHostnameDefault.ComputerName));
    }

    private void OpenConsole()
    {
        Uri address;
        try
        {
            address = ConsoleAddresses.Server(MachineConfiguration());
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            _services.Logger.LogWarning("The settings could not be read to find the console address: {Message}", exception.Message);
            address = new Uri(ConsoleAddresses.DefaultServerUrl);
        }

        TrayMenu.OpenInBrowser(address, _services);
    }

    private void SetStatus(TrayStatus status)
    {
        if (status == _status)
        {
            return;
        }

        _status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
