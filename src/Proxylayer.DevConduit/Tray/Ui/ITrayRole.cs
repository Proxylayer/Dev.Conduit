using System.Windows.Forms;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tray.Ui;

/// <summary>
/// One of the tray's two jobs: managing the installed service, or hosting local mode. The
/// application context swaps one for the other when the service is installed or removed.
/// </summary>
internal interface ITrayRole : IDisposable
{
    TrayRole Role { get; }

    TrayStatus Status { get; }

    /// <summary>What the local proxy last found about each imported server; empty on a server.</summary>
    IReadOnlyList<ClientTrustStatus> TrustStatuses { get; }

    /// <summary>Raised on the UI thread when <see cref="Status"/> changes.</summary>
    event EventHandler? StatusChanged;

    /// <summary>The role's menu items, built fresh each time the menu opens.</summary>
    IReadOnlyList<ToolStripItem> CreateMenuItems();

    /// <summary>Called every few seconds on the UI thread.</summary>
    void Poll();

    /// <summary>Called on the UI thread after the setup window saved settings.</summary>
    Task SettingsSavedAsync(SettingsTarget target, IWin32Window? owner);

    /// <summary>Stops whatever the role runs in this process. Never stops the Windows service.</summary>
    Task ShutdownAsync();
}
