using System.Drawing;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Windows.Forms;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tray.Ui.Setup;

/// <summary>
/// The server's own settings, saved to the machine settings file through the elevated helper:
/// the host name, port and client address of its HTTPS endpoint, whose certificate it manages
/// itself, the Entra ids and scope, who may sign in and which hosts it may call. Blank text
/// fields inherit, and show the inherited value greyed. A line in colour says what is left open
/// to everyone: any tenant user, or any reachable host. Also exports a <c>.conduit</c> for a
/// client, copies the Entra ids, scope and routes from another server's <c>.conduit</c>, and
/// restarts the service.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ServerTab : SetupTab
{
    private readonly SetupContext _context;
    private readonly TextBox _hostname = new();
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Width = 100, Anchor = AnchorStyles.Left };
    private readonly TextBox _clientAddress = new();
    private readonly TextBox _tenantId = new();
    private readonly TextBox _clientId = new();
    private readonly TextBox _managedIdentity = new();
    private readonly TextBox _scope = new();
    private readonly TextBox _allowedUsers = ListField();
    private readonly TextBox _allowedGroups = ListField();
    private readonly TextBox _allowedTargets = ListField();
    private readonly Label _warnings = new() { AutoSize = true, Dock = DockStyle.Bottom, ForeColor = Color.DarkOrange, Padding = new Padding(3, 4, 3, 4) };
    private readonly ProblemList _problems = new() { Dock = DockStyle.Bottom, Height = 64 };
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(3, 9, 3, 3) };
    private readonly Button _save = new() { Text = "Save (administrator)", AutoSize = true };
    private readonly Button _export = new() { Text = "Export client configuration...", AutoSize = true };
    private readonly Button _seed = new() { Text = "Copy settings from .conduit...", AutoSize = true };
    private readonly Button _restart = new() { Text = "Restart service", AutoSize = true };

    private SettingsLayers? _layers;
    private ServerSettingsModel? _model;
    private bool _loading;

    public ServerTab(SetupContext context)
    {
        _context = context;

        var fields = new FieldTable();
        fields.Add("Host name", _hostname, "The name clients reach this server by. The server serves HTTPS for it with a certificate it creates and renews itself. Blank uses this computer's name.");
        fields.Add("Port", _port);
        fields.Add("Client address", _clientAddress, "The IP address clients map the host name to in their hosts file. An export offers it.");
        fields.Add("Tenant ID", _tenantId);
        fields.Add("Client ID", _clientId, "The Dev.Conduit app registration.");
        fields.Add("Managed identity client ID", _managedIdentity, "The user-assigned identity on the VM, used as the app's credential.");
        fields.Add(
            "Scope",
            _scope,
            $"The delegated scope the app registration exposes. Blank uses {ApiScopeOptions.DefaultScope}. It must match the registration; an export hands it to clients.");
        fields.Add("Allowed users", _allowedUsers, "Entra object ids, one per line. With no users and no groups, any signed-in user of the tenant is allowed.");
        fields.Add("Allowed groups", _allowedGroups, "Entra group ids, one per line.");
        fields.Add(
            "Allowed targets",
            _allowedTargets,
            "Hosts this server may call, one per line: api.internal.example, *.internal.example, 10.0.0.0/16, each with an optional :port. Every route's target is allowed too. Empty allows any reachable host.");

        foreach (var field in new[] { _allowedUsers, _allowedGroups, _allowedTargets })
        {
            field.TextChanged += (_, _) => ShowWarnings();
        }

        _save.Click += (_, _) => Run(SaveAsync);
        _export.Click += (_, _) => Run(ExportAsync);
        _seed.Click += (_, _) => Run(SeedAsync);
        _restart.Click += (_, _) => Run(RestartAsync);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, WrapContents = true };
        buttons.Controls.AddRange([_save, _export, _seed, _restart, _status]);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(fields);

        // Docked last-added first: the buttons take the bottom edge, the problems sit above them
        // and the warnings above those.
        Controls.Add(scroll);
        Controls.Add(_warnings);
        Controls.Add(_problems);
        Controls.Add(buttons);
    }

    /// <summary>Raised after the service was restarted.</summary>
    public event EventHandler? ServiceChanged;

    public override string Title => "Server";

    public override void LoadFrom(SettingsLayers layers, bool keepEdits)
    {
        _loading = true;
        try
        {
            Read(layers, keepEdits);
        }
        finally
        {
            _loading = false;
        }

        ShowWarnings();
    }

    private void Read(SettingsLayers layers, bool keepEdits)
    {
        _layers = layers;
        _restart.Enabled = ServiceProbe.Status() == ServiceControllerStatus.Running;

        try
        {
            var view = layers.View(SettingsTarget.Machine);
            if (keepEdits && _model is not null)
            {
                ReadFields(_model);
                _model = _model.Reload(view);
            }
            else
            {
                _model = ServerSettingsModel.Load(view, ServerHostnameDefault.ComputerName());
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            _model = null;
            _save.Enabled = false;
            _problems.ShowError(exception.Message);
            return;
        }

        Bind(_hostname, _model.Hostname, ManagedSettingKeys.ServerHostname);
        if (string.IsNullOrWhiteSpace(_model.Inherited(ManagedSettingKeys.ServerHostname)) && _model.DefaultHostname is { } computerName)
        {
            _hostname.PlaceholderText = $"{computerName}  ({ServerHostnameDefault.Hint})";
        }

        _port.Value = Math.Clamp(_model.Port, (int)_port.Minimum, (int)_port.Maximum);
        Bind(_clientAddress, _model.ClientAddress, ManagedSettingKeys.ServerClientAddress);
        Bind(_tenantId, _model.TenantId, ManagedSettingKeys.TenantId);
        Bind(_clientId, _model.ClientId, ManagedSettingKeys.ClientId);
        Bind(_managedIdentity, _model.ManagedIdentityClientId, ManagedSettingKeys.ClientCredentials);
        Bind(_scope, _model.Scope, ManagedSettingKeys.Scopes);
        if (string.IsNullOrWhiteSpace(_model.Inherited(ManagedSettingKeys.Scopes)))
        {
            _scope.PlaceholderText = $"{ApiScopeOptions.DefaultScope}  (the default)";
        }

        Bind(_allowedUsers, _model.AllowedUserOids, ManagedSettingKeys.AllowedUserOids);
        Bind(_allowedGroups, _model.AllowedGroupIds, ManagedSettingKeys.AllowedGroupIds);
        Bind(_allowedTargets, _model.AllowedTargets, ManagedSettingKeys.AllowedTargets);

        _save.Enabled = true;
        _problems.ShowProblems(_model.Validate());
    }

    private void Bind(TextBox field, string value, string key)
    {
        field.Text = value;
        var inherited = _model?.Inherited(key);
        field.PlaceholderText = string.IsNullOrWhiteSpace(inherited)
            ? string.Empty
            : $"{inherited}  (from {_model!.InheritedFrom(key) ?? "a lower file"})";
    }

    private void ReadFields(ServerSettingsModel model)
    {
        model.Hostname = _hostname.Text;
        model.Port = (int)_port.Value;
        model.ClientAddress = _clientAddress.Text;
        model.TenantId = _tenantId.Text;
        model.ClientId = _clientId.Text;
        model.ManagedIdentityClientId = _managedIdentity.Text;
        model.Scope = _scope.Text;
        model.AllowedUserOids = _allowedUsers.Text;
        model.AllowedGroupIds = _allowedGroups.Text;
        model.AllowedTargets = _allowedTargets.Text;
    }

    /// <summary>Says what the settings as typed would leave open to everyone.</summary>
    private void ShowWarnings()
    {
        if (_loading)
        {
            return;
        }

        IReadOnlyList<string> warnings = _model is null ? [] : Warnings(_model);
        _warnings.Text = string.Join(Environment.NewLine, warnings.Select(warning => "Warning: " + warning));
        _warnings.Visible = warnings.Count > 0;
    }

    private IReadOnlyList<string> Warnings(ServerSettingsModel model)
    {
        ReadFields(model);
        return model.Warnings();
    }

    private async Task SaveAsync()
    {
        if (_model is null)
        {
            return;
        }

        // Against the file as it is now, not as it was when the page loaded.
        ReadFields(_model);
        var model = _model.Reload(_context.CreateLayers().View(SettingsTarget.Machine));
        var problems = model.Validate();
        _problems.ShowProblems(problems);
        if (!ConfirmDespite(problems, "The server settings"))
        {
            return;
        }

        var outcome = await _context.Saver.SaveAsync(SettingsTarget.Machine, model.ToChanges());
        if (outcome.Cancelled)
        {
            return;
        }

        if (!outcome.Succeeded)
        {
            ShowFailure(outcome.Message);
            return;
        }

        OnSaved(SettingsTarget.Machine);
        _status.Text = outcome.Message;
    }

    private async Task ExportAsync()
    {
        if (_layers is null)
        {
            return;
        }

        var machine = _layers.View(SettingsTarget.Machine);
        if (_model is not null && HasUnsavedChanges(machine))
        {
            ShowFailure("Save the server settings first: the export uses the saved host name.");
            return;
        }

        // With no host name set the export, like the service, uses this computer's name.
        var effective = ServerHostnameDefault.With(
            machine.Effective, ServerHostnameDefault.For(machine.Effective, applies: true, ServerHostnameDefault.ComputerName));
        var exported = await ClientConfigExportFlow.RunAsync(this, _context.Admin, effective);
        if (exported is not null)
        {
            _status.Text = $"Wrote {exported}.";
        }
    }

    /// <summary>Copies the tenant and client ids and the routes from a .conduit, through the elevated helper.</summary>
    private async Task SeedAsync()
    {
        var machine = _context.CreateLayers().View(SettingsTarget.Machine);
        if (_model is not null && HasUnsavedChanges(machine))
        {
            ShowFailure("Save the server settings first: copying from a .conduit replaces the tenant and client ids.");
            return;
        }

        if (await ServerSeedFlow.RunAsync(this, _context.Admin, machine.Document))
        {
            OnSaved(SettingsTarget.Machine);
            _status.Text = "Copied the tenant and client ids, the scope and the routes. Set the managed identity client ID if this server uses one.";
        }
    }

    private async Task RestartAsync()
    {
        var outcome = await _context.Admin.RunAsync(ServiceAdminCommand.RestartAction);
        _restart.Enabled = ServiceProbe.Status() == ServiceControllerStatus.Running;
        if (outcome.Cancelled)
        {
            return;
        }

        ServiceChanged?.Invoke(this, EventArgs.Empty);
        if (!outcome.Succeeded)
        {
            ShowFailure(outcome.Message);
            return;
        }

        _status.Text = "The service was restarted.";
    }

    /// <summary>True when the fields differ from what the file holds.</summary>
    private bool HasUnsavedChanges(SettingsView machine)
    {
        var saved = ServerSettingsModel.Load(machine).ToChanges().ToJson();
        var edited = ServerSettingsModel.Load(machine);
        ReadFields(edited);
        return !string.Equals(saved, edited.ToChanges().ToJson(), StringComparison.Ordinal);
    }

    private static TextBox ListField() =>
        new() { Multiline = true, Height = 54, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Font = new Font(FontFamily.GenericMonospace, 9f) };
}
