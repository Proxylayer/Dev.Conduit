using System.Runtime.Versioning;
using System.ServiceProcess;
using Proxylayer.DevConduit.Hosting;

namespace Proxylayer.DevConduit.Tray.Setup;

/// <summary>
/// Which of the Server page's service buttons apply, and the line shown beside them while the
/// MSI is installed. With the MSI in any role, install is off: it alone installs the service.
/// Uninstall is off only in its Server role, the one that installs the service; with the
/// Workstation role a service present came some other way and the tray may remove it.
/// Restarting a running service is always allowed.
/// </summary>
/// <param name="CanInstall">True when Install service applies.</param>
/// <param name="CanUninstall">True when Uninstall service applies.</param>
/// <param name="CanRestart">True when Restart service applies.</param>
/// <param name="InstallerNote">How the MSI bears on the service, or null when it is not installed.</param>
[SupportedOSPlatform("windows")]
public sealed record ServiceActions(bool CanInstall, bool CanUninstall, bool CanRestart, string? InstallerNote)
{
    /// <summary>The note while the MSI's service is installed.</summary>
    public const string ManagedByInstallerNote =
        "Installed by the Dev.Conduit MSI. Use Settings → Apps (or msiexec) to change or remove it.";

    /// <summary>The note while the MSI is installed in its Workstation role, which has no service.</summary>
    public const string WorkstationInstallerNote =
        "The Dev.Conduit MSI is installed in its Workstation role. To run the service, install the MSI with ROLE=Server.";

    /// <summary>The note while the MSI's Server role is installed but its service is missing.</summary>
    public const string MissingServiceNote =
        "The Dev.Conduit MSI installs this service. Repair it from Settings → Apps (or msiexec) to put the service back.";

    /// <summary>
    /// The note while the MSI is installed in its Workstation role and a service is present,
    /// which the MSI did not install.
    /// </summary>
    public const string StrayServiceNote =
        "This service was not installed by the Dev.Conduit MSI, which is installed in its Workstation role. " +
        "You can uninstall it here; to run a server, reinstall the MSI with ROLE=Server.";

    /// <summary>
    /// The buttons for a service in <paramref name="status"/>, null meaning not installed. An
    /// MSI with no saved role counts as Workstation, its default.
    /// </summary>
    public static ServiceActions For(ServiceControllerStatus? status, InstallerOwnership installer)
    {
        ArgumentNullException.ThrowIfNull(installer);

        var canInstall = status is null && !installer.OwnsServiceInstall;
        var canUninstall = status is not null && !installer.OwnsServiceUninstall;
        var canRestart = status == ServiceControllerStatus.Running;
        var note = !installer.IsInstalled ? null
            : status is null ? (installer.IsServerRole ? MissingServiceNote : WorkstationInstallerNote)
            : installer.IsServerRole ? ManagedByInstallerNote
            : StrayServiceNote;
        return new ServiceActions(canInstall, canUninstall, canRestart, note);
    }
}
