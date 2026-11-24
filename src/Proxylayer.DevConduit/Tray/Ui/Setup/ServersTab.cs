using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tray.Ui.Setup;

/// <summary>
/// The servers this workstation imported from <c>.conduit</c> files, one row per server,
/// with whether Windows trusts its certificate, when the local proxy last synced with it and
/// how many routes go to it. Importing, removing and updating the certificates (Windows trust
/// and this computer's client certificate) need an administrator, so each runs the elevated helper.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ServersTab : SetupTab
{
    private const string EmptyText = "Import a .conduit from your Dev.Conduit server.";

    private readonly SetupContext _context;
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        MultiSelect = false,
        HideSelection = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable
    };

    private readonly Label _empty = new()
    {
        Text = EmptyText + Environment.NewLine + Environment.NewLine
            + "Ask the server's administrator for its <hostname>.conduit file, then choose Import .conduit below.",
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = SystemColors.GrayText,
        Visible = false
    };

    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(3, 9, 3, 3) };
    private readonly Button _import = new() { Text = "Import .conduit...", AutoSize = true };
    private readonly Button _remove = new() { Text = "Remove", AutoSize = true, Enabled = false };
    private readonly Button _trust = new() { Text = "Update certificates", AutoSize = true, Visible = false };

    private IReadOnlyList<ImportedServerRow> _rows = [];
    private SettingsLayers? _layers;

    public ServersTab(SetupContext context)
    {
        _context = context;

        _list.Columns.Add("Name", 110);
        _list.Columns.Add("Host name", 220);
        _list.Columns.Add("Address", 110);
        _list.Columns.Add("Server certificate", 170);
        _list.Columns.Add("Last sync", 130);
        _list.Columns.Add("Routes", 60, HorizontalAlignment.Right);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        DpiChangedAfterParent += (_, _) => FitColumns();

        _import.Click += (_, _) => Run(ImportAsync);
        _remove.Click += (_, _) => Run(RemoveAsync);
        _trust.Click += (_, _) => Run(TrustAsync);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, WrapContents = true };
        buttons.Controls.AddRange([_import, _remove, _trust, _status]);

        var listArea = new Panel { Dock = DockStyle.Fill };
        listArea.Controls.Add(_list);
        listArea.Controls.Add(_empty);

        Controls.Add(listArea);
        Controls.Add(buttons);
    }

    public override string Title => "Servers";

    public override void LoadFrom(SettingsLayers layers, bool keepEdits)
    {
        _layers = layers;
        var selected = Selected?.Hostname;
        _list.Items.Clear();

        try
        {
            var effective = layers.View(SettingsTarget.User).Effective;
            var routing = RoutingConfigurationParser.Parse(effective.GetSection(DevConduitOptions.SectionName), effective.GetSection("AzureAd"));
            var reader = new ServerTrustReader(_context.TrustLayers);
            var now = DateTimeOffset.UtcNow;
            _rows = ImportedServerList.Build(
                routing,
                ReadRecords(),
                _context.HostsFile.ManagedAddress,
                hostname => reader.Read(hostname, now),
                _context.TrustStatuses());
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            _rows = [];
            _status.Text = exception.Message;
        }

        foreach (var row in _rows)
        {
            var item = new ListViewItem([
                row.Name.Length == 0 ? "(no remote)" : row.Name,
                row.Hostname,
                row.Address,
                row.Certificate,
                row.LastSync,
                row.Routes.ToString(System.Globalization.CultureInfo.CurrentCulture)])
            {
                Tag = row,
                Selected = string.Equals(row.Hostname, selected, StringComparison.Ordinal)
            };
            _list.Items.Add(item);
        }

        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0)
        {
            _list.Items[0].Selected = true;
        }

        _empty.Visible = _rows.Count == 0;
        _list.Visible = _rows.Count > 0;
        FitColumns();
        UpdateButtons();
    }

    /// <summary>Each column as wide as its header or its widest value, at the current DPI.</summary>
    private void FitColumns()
    {
        foreach (ColumnHeader column in _list.Columns)
        {
            column.Width = -2;
        }
    }

    private ImportedServerRow? Selected => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as ImportedServerRow : null;

    private void UpdateButtons()
    {
        var row = Selected;
        _remove.Enabled = row is not null;
        _trust.Visible = row?.TrustUpdateNeeded == true;
    }

    private List<ProxyConfigImportRecord> ReadRecords()
    {
        try
        {
            return ProxyConfigImportRecords.Read(_context.ClientDirectory.ImportRecordPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Checks the file here, unelevated, then hands a copy to the elevated helper.</summary>
    private async Task ImportAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import a Dev.Conduit server's client configuration",
            Filter = $"Dev.Conduit client configuration (*{ProxyConfigDocument.FileExtension}, *{ProxyConfigDocument.LegacyFileExtension})"
                + $"|*{ProxyConfigDocument.FileExtension};*{ProxyConfigDocument.LegacyFileExtension}",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        ValidatedProxyConfig config;
        try
        {
            var content = ProxyConfigTransfer.ReadLimited(dialog.FileName);
            if (!ProxyConfigValidator.TryValidate(content, DateTimeOffset.UtcNow, out var validated, out var error))
            {
                ShowFailure($"{Path.GetFileName(dialog.FileName)} was not imported:{Environment.NewLine}{Environment.NewLine}{error}");
                return;
            }

            config = validated;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowFailure($"The file could not be read: {exception.Message}");
            return;
        }

        var transfer = await Task.Run(() => ProxyConfigTransfer.WriteTempFile(dialog.FileName));
        ServiceAdminOutcome outcome;
        try
        {
            _status.Text = $"Importing {config.Hostname}...";
            outcome = await _context.Admin.RunAsync(ServiceAdminCommand.ImportAction, transfer);
        }
        finally
        {
            ProxyConfigTransfer.Delete(transfer);
        }

        if (!Finish(outcome, $"Imported {config.Hostname} as '{config.Remote}' with {config.Routes.Count} route(s)."))
        {
            return;
        }

        OnSaved(SettingsTarget.Machine);
    }

    /// <summary>
    /// Removes the server through the elevated helper, which takes the machine layer, then
    /// deletes this user's own layer for it and drops the user's own routes that still point at
    /// it, which would otherwise stop local mode starting.
    /// </summary>
    private async Task RemoveAsync()
    {
        if (Selected is not { } row || _layers is null)
        {
            return;
        }

        var name = row.Name.Length == 0 ? row.Hostname : $"{row.Name} ({row.Hostname})";
        var confirm = MessageBox.Show(
            this,
            $"Remove {name}?{Environment.NewLine}{Environment.NewLine}"
            + "Its routes, its line in the hosts file, its client certificate and the server certificates this app added to "
            + "Windows trust are removed. Import its .conduit again to use it later.",
            "Dev.Conduit setup",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        // Worked out before the machine file changes: which routes the removal takes with it.
        var record = ReadRecords().FirstOrDefault(entry => string.Equals(entry.Hostname, row.Hostname, StringComparison.OrdinalIgnoreCase));
        var removal = ImportedServerSettingsRemoval.Compute(_layers.View(SettingsTarget.Machine).Document, row.Hostname, record);

        var outcome = await _context.Admin.RunAsync(ServiceAdminCommand.ForgetAction, row.Hostname);
        if (!Finish(outcome, $"Removed {name}."))
        {
            return;
        }

        try
        {
            _context.TrustLayers.ForgetUser(row.Hostname);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowFailure($"The server was removed, but your own certificates for it could not be deleted: {exception.Message}");
        }

        var remote = removal.Remote ?? row.Name;
        if (UserRoutesCleanup.For(_layers.View(SettingsTarget.User).Document, remote, removal.RemovedRoutes) is { } cleanup)
        {
            var saved = await _context.Saver.SaveAsync(SettingsTarget.User, cleanup);
            if (!saved.Succeeded)
            {
                ShowFailure($"The server was removed, but your own routes for it could not be removed from {_context.Files.UserPath}: {saved.Message}");
            }
        }

        OnSaved(SettingsTarget.Machine);
    }

    private async Task TrustAsync()
    {
        if (Selected is not { TrustUpdateNeeded: true } row)
        {
            return;
        }

        var outcome = await _context.Admin.RunAsync(ServiceAdminCommand.TrustAction, row.Hostname);
        if (Finish(outcome, $"{row.Hostname}'s certificates are up to date.") && _layers is not null)
        {
            LoadFrom(_layers, keepEdits: true);
        }
    }

    /// <summary>Shows how an elevated action ended. True when it succeeded.</summary>
    private bool Finish(ServiceAdminOutcome outcome, string done)
    {
        if (outcome.Cancelled)
        {
            _status.Text = string.Empty;
            return false;
        }

        if (!outcome.Succeeded)
        {
            _status.Text = string.Empty;
            ShowFailure(outcome.Message);
            return false;
        }

        _status.Text = done;
        return true;
    }
}
