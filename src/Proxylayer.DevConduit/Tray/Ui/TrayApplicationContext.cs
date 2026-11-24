using System.Runtime.Versioning;
using System.Windows.Forms;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tray.Logging;
using Proxylayer.DevConduit.Tray.Setup;
using Proxylayer.DevConduit.Tray.Ui.Setup;
using Proxylayer.DevConduit.Tray.Updates;

namespace Proxylayer.DevConduit.Tray.Ui;

/// <summary>
/// The notification-area icon and its menu. Works out the role every few seconds (server
/// when the Dev.Conduit service is installed, workstation otherwise) and hands the menu and the
/// status to that role. Owns the setup and log windows, one of each at a time, and the update
/// items under the role's (<see cref="TrayUpdater"/>), which can exit the tray to install.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const int PollMilliseconds = 3_000;

    /// <summary>The notification-area tooltip limit.</summary>
    private const int TooltipLength = 127;

    private readonly TrayServices _services;
    private readonly SettingsSaver _saver;
    private readonly NotifyIcon _icon;
    private readonly TrayMenuPresenter _menu;
    private readonly TrayIconRenderer _icons = new();
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = PollMilliseconds };
    private readonly SynchronizationContext _ui;
    private readonly TrayUpdater _updates;

    private ITrayRole _role;
    private SetupWindow? _setup;
    private LogWindow? _logWindow;
    private Action? _balloonClick;
    private bool _exiting;

    public TrayApplicationContext(SingleInstance instance)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var settingsFiles = SettingsFiles.ForThisComputer();
        var admin = ServiceAdminClient.ForThisProcess();
        _services = new TrayServices(new LogRingBuffer(), settingsFiles, admin, Notify, ShowSetup, ShowLog);
        _saver = new SettingsSaver(settingsFiles, admin);
        _updates = new TrayUpdater(_services, ExitAsync);

        _role = CreateRole(TrayRoles.Detect());
        _role.StatusChanged += OnStatusChanged;

        // The menu is shown here rather than through NotifyIcon.ContextMenuStrip, which places it
        // at the cursor with the scale of the monitor it last sat on. The shell reports a keyboard
        // open (Shift+F10, the Apps key) as a right-button click too.
        _icon = new NotifyIcon();
        _menu = new TrayMenuPresenter(_icon, FillMenu);
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                ShowMenu();
            }
        };
        _icon.MouseDoubleClick += (_, _) => ShowSetup();
        _icon.BalloonTipClicked += (_, _) => _balloonClick?.Invoke();
        _icon.BalloonTipClosed += (_, _) => _balloonClick = null;
        UpdateIcon();
        _icon.Visible = true;

        _poll.Tick += (_, _) => Poll();
        _poll.Start();
        _updates.Changed += (_, _) => _menu.Refresh();
        _updates.Start();

        instance.OnActivated(() => _ui.Post(_ => ShowSetup(), null));
        Application.ApplicationExit += (_, _) => StopOnExit();
        Microsoft.Win32.SystemEvents.SessionEnded += OnSessionEnded;
        _services.Logger.LogInformation("The tray started in the {Role}.", TrayRoles.Describe(_role.Role).ToLowerInvariant());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Microsoft.Win32.SystemEvents.SessionEnded -= OnSessionEnded;
            _poll.Dispose();
            _updates.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
            _role.Dispose();
            _icons.Dispose();
            _setup?.Dispose();
            _logWindow?.Dispose();
        }

        base.Dispose(disposing);
    }

    private ITrayRole CreateRole(TrayRole role) =>
        role == TrayRole.Server ? new ServerRole(_services) : new WorkstationRole(_services);

    private void Poll()
    {
        if (_exiting)
        {
            return;
        }

        var detected = TrayRoles.Detect();
        if (detected != _role.Role)
        {
            TrayMenu.Run(() => SwitchRoleAsync(detected), "Switching role", _services);
            return;
        }

        _role.Poll();
    }

    private async Task SwitchRoleAsync(TrayRole role)
    {
        var previous = _role;
        if (previous.Role == role)
        {
            return;
        }

        _services.Logger.LogInformation("The Dev.Conduit service was {Change}; switching to the {Role}.",
            role == TrayRole.Server ? "installed" : "removed",
            TrayRoles.Describe(role).ToLowerInvariant());

        previous.StatusChanged -= OnStatusChanged;
        _role = CreateRole(role);
        _role.StatusChanged += OnStatusChanged;
        UpdateIcon();

        await previous.ShutdownAsync();
        previous.Dispose();
    }

    private void ShowMenu()
    {
        if (!_exiting)
        {
            _menu.Show();
        }
    }

    private void FillMenu(ToolStripItemCollection items)
    {
        items.Add(TrayMenu.Label($"Dev.Conduit - {TrayRoles.Describe(_role.Role)}", bold: true));
        items.Add(TrayMenu.Label(_role.Status.Text));
        items.Add(new ToolStripSeparator());
        items.AddRange([.. _role.CreateMenuItems()]);
        items.Add(new ToolStripSeparator());
        items.AddRange([.. _updates.CreateMenuItems()]);
        items.Add(new ToolStripSeparator());
        items.Add(TrayMenu.Item("Settings...", ShowSetup));
        items.Add(TrayMenu.Item("Exit", () => TrayMenu.Run(ExitAsync, "Exit", _services)));
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        UpdateIcon();
        _menu.Refresh();
    }

    private void UpdateIcon()
    {
        _icon.Icon = _icons.For(_role.Status.Kind);
        var tooltip = $"Dev.Conduit\n{TrayRoles.Describe(_role.Role)}: {_role.Status.Text}";
        _icon.Text = tooltip.Length <= TooltipLength ? tooltip : tooltip[..TooltipLength];
    }

    private void Notify(string title, string text, ToolTipIcon icon, Action? onClick)
    {
        _ui.Post(_ =>
        {
            _balloonClick = onClick;
            _icon.ShowBalloonTip(10_000, title, string.IsNullOrWhiteSpace(text) ? title : text, icon);
        }, null);
    }

    private void ShowSetup()
    {
        if (_setup is { IsDisposed: false })
        {
            _setup.Activate();
            if (_setup.WindowState == FormWindowState.Minimized)
            {
                _setup.WindowState = FormWindowState.Normal;
            }

            return;
        }

        _setup = new SetupWindow(SetupContext.ForThisComputer(_role.Role, _services.SettingsFiles, _saver, _services.Admin, () => _role.TrustStatuses, _updates));
        _setup.Saved += (_, target) => TrayMenu.Run(() => _role.SettingsSavedAsync(target, _setup), "Applying the settings", _services);
        _setup.ServiceChanged += (_, _) => Poll();
        _setup.FormClosed += (_, _) => _setup = null;
        _setup.Show();
        _setup.Activate();
    }

    private void ShowLog()
    {
        if (_logWindow is { IsDisposed: false })
        {
            _logWindow.Activate();
            return;
        }

        _logWindow = new LogWindow(_services.Log);
        _logWindow.FormClosed += (_, _) => _logWindow = null;
        _logWindow.Show();
        _logWindow.Activate();
    }

    /// <summary>Stops local mode, if this tray runs it, then ends the message loop. The Windows service is left alone.</summary>
    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _poll.Stop();
        _icon.Visible = false;
        _setup?.Close();
        _logWindow?.Close();

        await _role.ShutdownAsync();
        ExitThread();
    }

    private void OnSessionEnded(object? sender, Microsoft.Win32.SessionEndedEventArgs e) => StopOnExit();

    /// <summary>A sign-out or shutdown ends the loop without Exit; local mode is stopped here then.</summary>
    private void StopOnExit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        Task.Run(_role.ShutdownAsync).Wait(TimeSpan.FromSeconds(10));
    }
}
