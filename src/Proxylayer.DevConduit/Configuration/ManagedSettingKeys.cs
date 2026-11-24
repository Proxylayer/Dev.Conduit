namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// The configuration keys the tray writes to the settings files. Each is written whole: a
/// section such as <see cref="Routes"/> is replaced as one value, never key by key. The lists
/// are also allow-lists: the elevated helper holds a machine settings change from the setup
/// window to <see cref="Machine"/>, a <c>.conduit</c> import or removal to <see cref="Imported"/>,
/// and a server seeding its own settings from a <c>.conduit</c> to <see cref="Seed"/>, so none
/// can be used to write anything else into that file.
/// </summary>
public static class ManagedSettingKeys
{
    public const string TenantId = "AzureAd:TenantId";
    public const string ClientId = "AzureAd:ClientId";
    public const string ClientCredentials = "AzureAd:ClientCredentials";
    public const string Scopes = "AzureAd:Scopes";

    public const string ServerHostname = "DevConduit:Server:Hostname";
    public const string ServerPort = "DevConduit:Server:Port";
    public const string ServerClientAddress = "DevConduit:Server:ClientAddress";

    public const string AllowedUserOids = "DevConduit:AllowedUserOids";
    public const string AllowedGroupIds = "DevConduit:AllowedGroupIds";
    public const string AllowedTargets = "DevConduit:Route:AllowedTargets";

    public const string Remotes = "DevConduit:Remotes";
    public const string Routes = "DevConduit:Routes";

    public const string LocalPort = "DevConduit:Local:Port";
    public const string LocalForwardProxyEnabled = "DevConduit:Local:ForwardProxyEnabled";
    public const string LocalForwardProxyPort = "DevConduit:Local:ForwardProxyPort";
    public const string LocalCaptureEnabled = "DevConduit:Local:CaptureEnabled";

    public const string TrayAutoStart = "DevConduit:Tray:AutoStart";

    public const string UpdateAutoInstall = "DevConduit:Update:AutoInstall";

    /// <summary>
    /// The keys the setup window's Server and Routes pages write to the machine settings file,
    /// and the server's Install updates automatically switch.
    /// </summary>
    public static IReadOnlySet<string> Machine { get; } = new HashSet<string>(
        [
            TenantId, ClientId, ClientCredentials, Scopes,
            ServerHostname, ServerPort, ServerClientAddress,
            AllowedUserOids, AllowedGroupIds, AllowedTargets,
            Routes,
            UpdateAutoInstall
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>The keys a <c>.conduit</c> import, or removing an imported server, writes to the machine settings file.</summary>
    public static IReadOnlySet<string> Imported { get; } = new HashSet<string>(
        [
            TenantId, ClientId,
            Remotes, Routes
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The keys a server seeding its own settings from a <c>.conduit</c> writes to the machine
    /// settings file: the Entra ids, the scope and the routes. Never its host name, port or client address.
    /// </summary>
    public static IReadOnlySet<string> Seed { get; } = new HashSet<string>(
        [
            TenantId, ClientId, Scopes,
            Routes
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The keys the setup window's Routes and Local proxy pages, and the tray's Start proxy
    /// automatically and Install updates automatically items, write to the user settings file.
    /// </summary>
    public static IReadOnlySet<string> User { get; } = new HashSet<string>(
        [
            Routes,
            LocalPort, LocalForwardProxyEnabled, LocalForwardProxyPort, LocalCaptureEnabled,
            TrayAutoStart,
            UpdateAutoInstall
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>The keys in <paramref name="changes"/> that are not in <paramref name="allowed"/>.</summary>
    public static IReadOnlyList<string> Unmanaged(SettingsChangeSet changes, IReadOnlySet<string> allowed) =>
        changes.Keys.Where(key => !allowed.Contains(key)).ToList();
}
