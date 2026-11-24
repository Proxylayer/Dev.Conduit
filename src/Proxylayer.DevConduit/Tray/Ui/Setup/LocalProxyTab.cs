using System.Runtime.Versioning;
using System.Windows.Forms;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tray.Ui.Setup;

/// <summary>The workstation's local-mode listeners and capture, saved to the user settings file.</summary>
[SupportedOSPlatform("windows")]
internal sealed class LocalProxyTab : SetupTab
{
    private readonly SetupContext _context;
    private readonly SettingsSaver _saver;
    private readonly NumericUpDown _port = PortField();
    private readonly CheckBox _forwardProxyEnabled = new() { Text = "Run the forward proxy (HTTP_PROXY)", AutoSize = true };
    private readonly NumericUpDown _forwardProxyPort = PortField();
    private readonly CheckBox _captureEnabled = new() { Text = "Capture relayed traffic locally as well", AutoSize = true };
    private readonly ProblemList _problems = new() { Dock = DockStyle.Top, Height = 80 };
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(3, 9, 3, 3) };
    private readonly Button _save = new() { Text = "Save", AutoSize = true };
    private LocalProxySettingsModel? _model;
    private SettingsLayers? _layers;

    public LocalProxyTab(SetupContext context)
    {
        _context = context;
        _saver = context.Saver;

        var fields = new FieldTable();
        fields.Add("Console port", _port, "The local console and reverse proxy listen here, on loopback only.");
        fields.Add("Forward proxy", _forwardProxyEnabled);
        fields.Add("Forward proxy port", _forwardProxyPort, "Point HTTP_PROXY at http://127.0.0.1:<this port>.");
        fields.Add("Capture", _captureEnabled, "Off by default: the remote instance already keeps the record.");

        _forwardProxyEnabled.CheckedChanged += (_, _) => _forwardProxyPort.Enabled = _forwardProxyEnabled.Checked;
        _save.Click += (_, _) => Run(SaveAsync);
        var revert = new Button { Text = "Revert", AutoSize = true };
        revert.Click += (_, _) =>
        {
            if (_layers is not null)
            {
                LoadFrom(_layers, keepEdits: false);
            }
        };

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false };
        buttons.Controls.AddRange([_save, revert, _status]);

        Controls.Add(buttons);
        Controls.Add(_problems);
        Controls.Add(fields);
    }

    public override string Title => "Local proxy";

    public override void LoadFrom(SettingsLayers layers, bool keepEdits)
    {
        _layers = layers;

        try
        {
            var view = layers.View(SettingsTarget.User);
            if (keepEdits && _model is not null)
            {
                ReadFields(_model);
                _model = _model.Reload(view);
            }
            else
            {
                _model = LocalProxySettingsModel.Load(view);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            _model = null;
            _save.Enabled = false;
            _problems.ShowError(exception.Message);
            return;
        }

        _port.Value = Math.Clamp(_model.Port, (int)_port.Minimum, (int)_port.Maximum);
        _forwardProxyEnabled.Checked = _model.ForwardProxyEnabled;
        _forwardProxyPort.Value = Math.Clamp(_model.ForwardProxyPort, (int)_forwardProxyPort.Minimum, (int)_forwardProxyPort.Maximum);
        _forwardProxyPort.Enabled = _model.ForwardProxyEnabled;
        _captureEnabled.Checked = _model.CaptureEnabled;
        _save.Enabled = true;
        _status.Text = string.Empty;
        _problems.ShowProblems(_model.Validate());
    }

    private async Task SaveAsync()
    {
        if (_model is null)
        {
            return;
        }

        // Against the file as it is now, not as it was when the page loaded.
        ReadFields(_model);
        var model = _model.Reload(_context.CreateLayers().View(SettingsTarget.User));
        var problems = model.Validate();
        _problems.ShowProblems(problems);
        if (problems.Count > 0)
        {
            ShowFailure("Local mode would refuse to start with these settings. Correct them, then save again.");
            return;
        }

        var outcome = await _saver.SaveAsync(SettingsTarget.User, model.ToChanges());
        if (outcome.Cancelled)
        {
            return;
        }

        if (!outcome.Succeeded)
        {
            ShowFailure(outcome.Message);
            return;
        }

        OnSaved(SettingsTarget.User);
        _status.Text = outcome.Message;
    }

    private void ReadFields(LocalProxySettingsModel model)
    {
        model.Port = (int)_port.Value;
        model.ForwardProxyEnabled = _forwardProxyEnabled.Checked;
        model.ForwardProxyPort = (int)_forwardProxyPort.Value;
        model.CaptureEnabled = _captureEnabled.Checked;
    }

    private static NumericUpDown PortField() => new() { Minimum = 1, Maximum = 65535, Width = 100, Anchor = AnchorStyles.Left };
}
