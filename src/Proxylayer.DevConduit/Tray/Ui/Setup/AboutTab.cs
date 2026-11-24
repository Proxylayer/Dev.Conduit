using System.Runtime.Versioning;
using System.Text;
using System.Windows.Forms;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tray.Setup;
using Proxylayer.DevConduit.Updates;

namespace Proxylayer.DevConduit.Tray.Ui.Setup;

/// <summary>
/// Where everything is: the role, the version, the install folder, the settings files and whether
/// each exists, and the data folder; and updates: the feed, the last check, the release available
/// and its state, with Check for updates and the Install updates automatically switch.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class AboutTab : SetupTab
{
    private readonly SetupContext _context;
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable
    };

    private readonly CheckBox _autoInstall = new() { Text = "Install updates automatically", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
    private readonly Button _checkNow = new() { Text = "Check for updates", AutoSize = true };
    private SettingsLayers? _layers;
    private bool _loading;

    public AboutTab(SetupContext context)
    {
        _context = context;
        _list.Columns.Add("Item", 150);
        _list.Columns.Add("Value", 520);
        _list.Columns.Add("Notes", 140);

        var copy = new Button { Text = "Copy", AutoSize = true };
        copy.Click += (_, _) => Clipboard.SetText(AsText());
        DpiChangedAfterParent += (_, _) => FitColumns();
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom };
        buttons.Controls.Add(copy);

        if (context.Updates is { } updates)
        {
            _checkNow.Enabled = updates.IsEnabled;
            _checkNow.Click += (_, _) => Run(() => updates.CheckAsync(userAsked: true));
            _autoInstall.Enabled = updates.IsEnabled;
            _autoInstall.CheckedChanged += (_, _) =>
            {
                if (!_loading && _autoInstall.Checked != updates.AutoInstall)
                {
                    Run(() => updates.SetAutoInstallAsync(_autoInstall.Checked));
                }
            };
            updates.Changed += OnUpdatesChanged;
            Disposed += (_, _) => updates.Changed -= OnUpdatesChanged;
            buttons.Controls.Add(_checkNow);
            buttons.Controls.Add(_autoInstall);
        }

        Controls.Add(_list);
        Controls.Add(buttons);
    }

    public override string Title => "About";

    public override void LoadFrom(SettingsLayers layers, bool keepEdits)
    {
        _layers = layers;
        _list.Items.Clear();
        var server = _context.Role == TrayRole.Server;

        Add("Role", TrayRoles.Describe(_context.Role), server ? "the Dev.Conduit service is installed" : "the tray runs the local proxy");
        Add("Version", ReleaseVersion.CurrentDisplay, string.Empty);
        AddUpdates();
        Add("Install folder", AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), string.Empty);

        var machineNote = File.Exists(layers.Files.MachinePath) ? "exists" : "absent";
        if (File.Exists(layers.Files.MachinePath) && MachineSettingsAccess.Check(layers.Files.MachinePath) is { } problem)
        {
            machineNote = "REFUSED: " + problem;
        }

        Add("Machine settings", layers.Files.MachinePath, machineNote);
        Add("User settings", layers.Files.UserPath, (File.Exists(layers.Files.UserPath) ? "exists" : "absent") + (server ? "; not read by the service" : string.Empty));

        try
        {
            var configured = layers.View(server ? SettingsTarget.Machine : SettingsTarget.User).Effective[DataRootDefault.Key];
            var dataRoot = DataRootDefault.Resolve(configured, usesMachineFolder: server);
            Add("Data folder", dataRoot, Directory.Exists(dataRoot) ? "exists" : "absent");
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            Add("Data folder", "(the settings could not be read)", exception.Message);
        }

        FitColumns();
    }

    /// <summary>The update rows, and the switch set from the settings without saving.</summary>
    private void AddUpdates()
    {
        if (_context.Updates is not { } updates)
        {
            return;
        }

        var status = updates.Status;
        Add("Update feed", status.Feed, updates.IsServer ? "checked by the service" : string.Empty);
        Add("Last update check", status.LastCheck?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? "not yet", string.Empty);
        Add("Available update", status.Available is { } available ? $"v{available}" : "none",
            status.Ready ? (status.NextInstall is { } next ? $"installs {next.ToLocalTime():g}" : "downloaded and verified") : string.Empty);
        Add("Update status", status.Message, string.Empty);

        _loading = true;
        try
        {
            _autoInstall.Checked = updates.AutoInstall;
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnUpdatesChanged(object? sender, EventArgs e)
    {
        if (_layers is not null && !IsDisposed)
        {
            LoadFrom(_layers, keepEdits: true);
        }
    }

    /// <summary>Each column as wide as its header or its widest value, at the current DPI.</summary>
    private void FitColumns()
    {
        foreach (ColumnHeader column in _list.Columns)
        {
            column.Width = -2;
        }
    }

    private void Add(string item, string value, string note) => _list.Items.Add(new ListViewItem([item, value, note]));

    private string AsText()
    {
        var text = new StringBuilder();
        foreach (ListViewItem item in _list.Items)
        {
            text.AppendLine(string.Join('\t', item.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(sub => sub.Text)));
        }

        return text.ToString();
    }
}
