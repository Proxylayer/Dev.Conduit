using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tray.Ui.Setup;

/// <summary>
/// The setup window, one set of pages per role. A workstation sees its imported servers, its
/// routes and its local proxy; a server sees its own settings and the routes it exports. Both
/// have About. It never edits the appsettings files, which an update replaces: every page saves
/// to the machine or user settings file, and every page reloads from the files after any of them saves.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SetupWindow : Form
{
    private static readonly Size LogicalSize = new(900, 600);
    private static readonly Size LogicalMinimumSize = new(720, 460);

    private readonly SetupContext _context;
    private readonly List<SetupTab> _tabs = [];

    public SetupWindow(SetupContext context)
    {
        _context = context;

        // Laid out in 96-DPI pixels and scaled to the monitor the window opens on (see OnLoad),
        // and again by Windows Forms when it moves to one with another scale (the tray runs
        // per-monitor aware).
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"Dev.Conduit setup - {TrayRoles.Describe(context.Role)}";
        StartPosition = FormStartPosition.CenterScreen;
        Size = LogicalSize;
        ShowInTaskbar = true;

        if (context.Role == TrayRole.Server)
        {
            var server = new ServerTab(context);
            server.ServiceChanged += (_, _) => ServiceChanged?.Invoke(this, EventArgs.Empty);
            _tabs.Add(server);
            _tabs.Add(new RoutesTab(context, SettingsTarget.Machine));
        }
        else
        {
            _tabs.Add(new ServersTab(context));
            _tabs.Add(new RoutesTab(context, SettingsTarget.User));
            _tabs.Add(new LocalProxyTab(context));
        }

        _tabs.Add(new AboutTab(context));

        var pages = new TabControl { Dock = DockStyle.Fill };
        foreach (var tab in _tabs)
        {
            tab.Saved += OnTabSaved;
            var page = new TabPage(tab.Title) { UseVisualStyleBackColor = true };
            page.Controls.Add(tab);
            pages.TabPages.Add(page);
        }

        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        buttons.Controls.Add(close);

        Controls.Add(pages);
        Controls.Add(buttons);
        CancelButton = close;

        Load += (_, _) => Reload();
    }

    /// <summary>Raised after a page saved a settings file, or imported or removed a server (the machine file).</summary>
    public event EventHandler<SettingsTarget>? Saved;

    /// <summary>Raised after the Server page restarted the service.</summary>
    public event EventHandler? ServiceChanged;

    /// <summary>
    /// Reads every page from the files again, for example after the local proxy synced. Values
    /// typed into a page and not yet saved are kept, except on <paramref name="saved"/>, the page
    /// that has just saved them.
    /// </summary>
    public void Reload(SetupTab? saved = null)
    {
        var layers = _context.CreateLayers();
        foreach (var tab in _tabs)
        {
            tab.LoadFrom(layers, keepEdits: !ReferenceEquals(tab, saved));
        }
    }

    /// <summary>
    /// Sizes the window for the monitor it opened on. Automatic scaling scales the controls but
    /// leaves a top-level window's own size in device pixels, so it is set here once the window
    /// knows its DPI.
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        MinimumSize = LogicalToDeviceUnits(LogicalMinimumSize);
        Size = LogicalToDeviceUnits(LogicalSize);
        if (StartPosition == FormStartPosition.CenterScreen)
        {
            CenterToScreen();
        }
    }

    private void OnTabSaved(object? sender, SettingsTarget target)
    {
        Reload(sender as SetupTab);
        Saved?.Invoke(this, target);
    }
}
