using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tray.Ui.Setup;

/// <summary>
/// The routes, as one grid. On a workstation each route names the imported server that carries
/// it, and the page saves to the user settings file; on a server the routes are what it exports
/// to clients, saved to the machine settings file. Rows defined by a lower file (an import, on a
/// workstation) are grey until changed; they can be changed or switched off, but not deleted.
/// Every edit is checked with the routing parser a launch uses.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RoutesTab : SetupTab
{
    private readonly SettingsTarget _target;
    private readonly SetupContext _context;
    private readonly SettingsSaver _saver;
    private readonly Label _intro = new() { AutoSize = true, MaximumSize = new Size(820, 0), Margin = new Padding(3, 3, 3, 8) };
    private readonly DataGridView _routes;
    private readonly DataGridViewComboBoxColumn? _server;
    private readonly ProblemList _problems = new();
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(3, 9, 3, 3) };
    private readonly Button _save = new() { AutoSize = true };
    private readonly Button _revert = new() { Text = "Revert", AutoSize = true };
    private readonly System.Windows.Forms.Timer _validate = new() { Interval = 400 };

    private SettingsLayers? _layers;
    private RoutingEditModel? _model;
    private bool _loading;

    public RoutesTab(SetupContext context, SettingsTarget target)
    {
        _target = target;
        _context = context;
        _saver = context.Saver;
        _save.Text = target == SettingsTarget.Machine ? "Save (administrator)" : "Save";

        _routes = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersWidth = 28,
            BackgroundColor = SystemColors.Window,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2
        };

        _routes.Columns.AddRange(
            new DataGridViewCheckBoxColumn
            {
                DataPropertyName = nameof(RouteRow.Enabled),
                HeaderText = "On",
                ToolTipText = "Clear to switch a route off. A route from an import cannot be deleted, only switched off.",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                Width = 36
            },
            TextColumn(nameof(RouteRow.Name), "Name", 20, "The route's name: /route/<name>/... reaches it."),
            TextColumn(nameof(RouteRow.Target), "Target", 50, "The upstream origin the route stands for, for example http://10.0.0.5:8080."));

        if (target == SettingsTarget.User)
        {
            _server = new DataGridViewComboBoxColumn
            {
                DataPropertyName = nameof(RouteRow.Remote),
                HeaderText = "Server",
                ToolTipText = "The imported server that carries the route. With one server, a route naming none goes to it.",
                FillWeight = 22,
                FlatStyle = FlatStyle.Flat,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox
            };
            _server.Items.Add(string.Empty);
            _routes.Columns.Add(_server);
        }

        _routes.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_routes.IsCurrentCellDirty && _routes.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)
            {
                _routes.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _routes.CellValueChanged += (_, _) => Changed();
        _routes.RowsRemoved += (_, _) => Changed();
        _routes.UserAddedRow += (_, _) => Changed();
        _routes.UserDeletingRow += OnUserDeletingRow;
        _routes.CellBeginEdit += OnCellBeginEdit;
        _routes.CellFormatting += OnCellFormatting;
        _routes.DataError += (_, e) => e.ThrowException = false;

        _validate.Tick += (_, _) =>
        {
            _validate.Stop();
            Check();
        };

        _save.Click += (_, _) => Run(SaveAsync);
        _revert.Click += (_, _) =>
        {
            if (_layers is not null)
            {
                LoadFrom(_layers, keepEdits: false);
            }
        };

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        buttons.Controls.AddRange([_save, _revert, _status]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 75));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_intro);
        layout.Controls.Add(_routes);
        layout.Controls.Add(_problems);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        Disposed += (_, _) => _validate.Dispose();
    }

    public override string Title => "Routes";

    public override void LoadFrom(SettingsLayers layers, bool keepEdits)
    {
        _layers = layers;
        if (_routes.IsCurrentCellInEditMode)
        {
            _routes.EndEdit();
        }

        _loading = true;
        try
        {
            var view = layers.View(_target);
            _model = keepEdits && _model is not null ? _model.Reload(view) : RoutingEditModel.Load(view);
            _intro.Text = _target == SettingsTarget.Machine
                ? "The routes this server forwards, /route/<name>/... to the target. An export gives them to clients."
                : Intro(_model);

            _routes.DataSource = _model.Routes;
            RefreshServers();
            _save.Enabled = true;
            _status.Text = string.Empty;
            Check();
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            _model = null;
            _routes.DataSource = null;
            _save.Enabled = false;
            _problems.ShowError(exception.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    private static string Intro(RoutingEditModel model) =>
        model.ServerNames.Count switch
        {
            0 => "No server is imported yet. Import a .conduit on the Servers page; its routes appear here.",
            1 => $"Routes go to {model.ServerNames[0]}. Grey rows come from an import: change one and only the change is saved, "
                 + "to your own settings; delete one and it is switched off.",
            _ => "Each route names the server that carries it. Grey rows come from an import: change one and only the change is saved, "
                 + "to your own settings; delete one and it is switched off."
        };

    private void Changed()
    {
        if (_loading)
        {
            return;
        }

        _status.Text = string.Empty;
        _validate.Stop();
        _validate.Start();
    }

    private void Check()
    {
        if (_model is null)
        {
            return;
        }

        if (!_routes.IsCurrentCellInEditMode)
        {
            RefreshServers();
        }

        _routes.Invalidate();
        _problems.ShowProblems(_model.Validate());
    }

    /// <summary>Fills the server drop-down with the imported servers, keeping any name a route already gives.</summary>
    private void RefreshServers()
    {
        if (_server is null || _model is null)
        {
            return;
        }

        var choices = new List<string> { string.Empty };
        choices.AddRange(_model.ServerNames);
        choices.AddRange(_model.Routes
            .Select(route => route.Remote.Trim())
            .Where(remote => remote.Length > 0 && !choices.Contains(remote, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase));

        if (!_server.Items.Cast<string>().SequenceEqual(choices))
        {
            _server.Items.Clear();
            _server.Items.AddRange([.. choices]);
        }
    }

    private async Task SaveAsync()
    {
        if (_model is null)
        {
            return;
        }

        if (_routes.IsCurrentCellInEditMode)
        {
            _routes.EndEdit();
        }

        var rowProblems = _model.RowProblems();
        if (rowProblems.Count > 0)
        {
            _problems.ShowProblems(rowProblems);
            ShowFailure("Correct the rows listed under the grid, then save again.");
            return;
        }

        // Against the file as it is now, not as it was when the page loaded.
        var model = _model.Reload(_context.CreateLayers().View(_target));
        var problems = model.Validate();
        _problems.ShowProblems(problems);
        if (!ConfirmDespite(problems, "The routes"))
        {
            return;
        }

        _save.Enabled = false;
        try
        {
            var outcome = await _saver.SaveAsync(_target, model.ToChanges());
            if (outcome.Cancelled)
            {
                return;
            }

            if (!outcome.Succeeded)
            {
                ShowFailure(outcome.Message);
                return;
            }

            OnSaved(_target);
            _status.Text = outcome.Message;
        }
        finally
        {
            _save.Enabled = _model is not null;
        }
    }

    private void OnUserDeletingRow(object? sender, DataGridViewRowCancelEventArgs e)
    {
        if (e.Row?.DataBoundItem is not RouteRow { InheritedFrom: { } source } route)
        {
            return;
        }

        e.Cancel = true;
        route.Enabled = false;
        _status.Text = $"The route '{route.Name}' comes from {source}, so it was switched off instead.";
        _routes.Refresh();
        Changed();
    }

    private void OnCellBeginEdit(object? sender, DataGridViewCellCancelEventArgs e)
    {
        // Renaming a row from a lower file would leave the original in place under its old name.
        e.Cancel = e.RowIndex >= 0
            && _routes.Columns[e.ColumnIndex].DataPropertyName == nameof(RouteRow.Name)
            && _routes.Rows[e.RowIndex].DataBoundItem is RouteRow { InheritedFrom: not null };
    }

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.CellStyle is null || _model is null || _routes.Rows[e.RowIndex].DataBoundItem is not RouteRow row)
        {
            return;
        }

        // A route naming no server goes to the only one, so the cell shows it.
        if (_server is not null
            && e.ColumnIndex == _server.Index
            && row.Remote.Trim().Length == 0
            && _model.DefaultServer.Length > 0)
        {
            e.Value = _model.DefaultServer;
            e.FormattingApplied = true;
        }

        if (_model.MatchesInherited(row) || !row.Enabled)
        {
            e.CellStyle.ForeColor = SystemColors.GrayText;
        }

        if (!row.Enabled && e.CellStyle.Font is { } font)
        {
            e.CellStyle.Font = new Font(font, FontStyle.Italic);
        }
    }

    private static DataGridViewTextBoxColumn TextColumn(string property, string header, float weight, string tip) =>
        new()
        {
            DataPropertyName = property,
            HeaderText = header,
            ToolTipText = tip,
            FillWeight = weight
        };
}
