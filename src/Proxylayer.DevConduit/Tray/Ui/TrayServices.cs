using System.Runtime.Versioning;
using System.Windows.Forms;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tray.Logging;

namespace Proxylayer.DevConduit.Tray.Ui;

/// <summary>
/// What the tray's two roles share with the application context: the log, the settings
/// files, the elevated helper, and the ways to reach the user.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TrayServices
{
    public TrayServices(
        LogRingBuffer log,
        SettingsFiles settingsFiles,
        ServiceAdminClient admin,
        Action<string, string, ToolTipIcon, Action?> notify,
        Action openSettings,
        Action openLog)
    {
        Log = log;
        LogProvider = new RingBufferLoggerProvider(log);
        Logger = LogProvider.CreateLogger("Dev.Conduit.Tray");
        SettingsFiles = settingsFiles;
        Admin = admin;
        Notify = notify;
        OpenSettings = openSettings;
        OpenLog = openLog;
    }

    public LogRingBuffer Log { get; }

    public RingBufferLoggerProvider LogProvider { get; }

    public ILogger Logger { get; }

    public SettingsFiles SettingsFiles { get; }

    public ServiceAdminClient Admin { get; }

    /// <summary>Shows a balloon: title, text, icon, and what clicking it does.</summary>
    public Action<string, string, ToolTipIcon, Action?> Notify { get; }

    public Action OpenSettings { get; }

    public Action OpenLog { get; }
}
