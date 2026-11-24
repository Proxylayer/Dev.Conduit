using System.Runtime.Versioning;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tray.Setup;
using Proxylayer.DevConduit.Tray.Updates;

namespace Proxylayer.DevConduit.Tray.Ui.Setup;

/// <summary>
/// What the setup window works with: the role it shows, the settings files and how they are
/// saved, the elevated helper, where imported servers live, and the running local proxy's view
/// of them. <see cref="ForThisComputer"/> is the real machine; anything else can host the
/// window over other files.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SetupContext
{
    public SetupContext(
        TrayRole role,
        SettingsFiles files,
        SettingsSaver saver,
        ServiceAdminClient admin,
        ClientTrustLayers trustLayers,
        HostsFile hostsFile,
        Func<IReadOnlyList<ClientTrustStatus>> trustStatuses,
        string contentRoot,
        string environmentName,
        TrayUpdater? updates = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        Updates = updates;

        Role = role;
        Files = files ?? throw new ArgumentNullException(nameof(files));
        Saver = saver ?? throw new ArgumentNullException(nameof(saver));
        Admin = admin ?? throw new ArgumentNullException(nameof(admin));
        TrustLayers = trustLayers ?? throw new ArgumentNullException(nameof(trustLayers));
        HostsFile = hostsFile ?? throw new ArgumentNullException(nameof(hostsFile));
        TrustStatuses = trustStatuses ?? throw new ArgumentNullException(nameof(trustStatuses));
        ContentRoot = contentRoot;
        EnvironmentName = environmentName;
    }

    public TrayRole Role { get; }

    public SettingsFiles Files { get; }

    public SettingsSaver Saver { get; }

    public ServiceAdminClient Admin { get; }

    /// <summary>Imported servers' client trust: the machine layer and this user's own.</summary>
    public ClientTrustLayers TrustLayers { get; }

    /// <summary>The machine layer, with the import and root store records.</summary>
    public ClientCertificateDirectory ClientDirectory => TrustLayers.Machine;

    public HostsFile HostsFile { get; }

    /// <summary>The running local proxy's latest sync results; empty on a server or while it is stopped.</summary>
    public Func<IReadOnlyList<ClientTrustStatus>> TrustStatuses { get; }

    /// <summary>The folder holding the appsettings files the role's launches read.</summary>
    public string ContentRoot { get; }

    /// <summary>The environment name those launches use, which picks <c>appsettings.{name}.json</c>.</summary>
    public string EnvironmentName { get; }

    /// <summary>The tray's updater, for the About page; null where the window is hosted without a tray.</summary>
    public TrayUpdater? Updates { get; }

    /// <summary>
    /// The real machine. The service always runs from the exe's folder with its own environment
    /// name; local mode resolves its content root as a console run does, with this process's.
    /// </summary>
    public static SetupContext ForThisComputer(
        TrayRole role,
        SettingsFiles files,
        SettingsSaver saver,
        ServiceAdminClient admin,
        Func<IReadOnlyList<ClientTrustStatus>> trustStatuses,
        TrayUpdater? updates = null) =>
        new(
            role,
            files,
            saver,
            admin,
            ClientTrustLayers.ForThisUser(),
            HostsFile.ForThisComputer(),
            trustStatuses,
            role == TrayRole.Server
                ? AppContext.BaseDirectory
                : DevConduitApplication.ResolveContentRoot(runningAsService: false) ?? Directory.GetCurrentDirectory(),
            role == TrayRole.Server ? SettingsLayers.ServiceEnvironmentName() : SettingsLayers.ProcessEnvironmentName(),
            updates);

    public SettingsLayers CreateLayers() => new(ContentRoot, EnvironmentName, Files);
}
