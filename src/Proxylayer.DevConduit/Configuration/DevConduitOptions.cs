using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// Settings bound from the "DevConduit" configuration section.
/// </summary>
public sealed class DevConduitOptions
{
    public const string SectionName = "DevConduit";

    /// <summary>
    /// Which role this process plays. Overridable at launch with <c>--mode server|local</c>.
    /// </summary>
    public DevConduitMode Mode { get; set; } = DevConduitMode.Server;

    /// <summary>Settings that apply only when <see cref="Mode"/> is <see cref="DevConduitMode.Local"/>.</summary>
    public LocalModeOptions Local { get; set; } = new();

    /// <summary>The server's host name and HTTPS port, for the app-managed certificate. Server mode only.</summary>
    public ServerHostOptions Server { get; set; } = new();

    /// <summary>
    /// Root directory for per-user JSON storage, keys and certificates. Empty means
    /// %LOCALAPPDATA%\Proxylayer\Dev.Conduit, or %ProgramData%\Proxylayer\Dev.Conduit for a server (see <see cref="DataRootDefault"/>).
    /// </summary>
    public string DataRoot { get; set; } = string.Empty;

    /// <summary>Maximum number of history entries retained per user; the oldest are pruned on write.</summary>
    public int MaxHistoryEntries { get; set; } = 500;

    /// <summary>Maximum number of bytes of a request/response body persisted to history.</summary>
    public int MaxStoredBodyBytes { get; set; } = 1_048_576;

    /// <summary>
    /// Maximum number of routed-traffic entries retained per user. Kept separate from
    /// <see cref="MaxHistoryEntries"/> so forwarded traffic can never evict console history.
    /// </summary>
    public int MaxRoutedHistoryEntries { get; set; } = 5_000;

    /// <summary>Maximum number of bytes of a routed request/response body retained for capture.</summary>
    public int MaxRoutedStoredBodyBytes { get; set; } = 2_097_152;

    /// <summary>Settings for the transparent /route forwarding endpoint.</summary>
    public RouteForwardingOptions Route { get; set; } = new();

    /// <summary>Header names always treated as secrets, regardless of the per-header flag.</summary>
    public string[] SecretHeaderNames { get; set; } =
    [
        "Authorization",
        "Cookie",
        "Set-Cookie",
        "Proxy-Authorization",
        "X-Api-Key",
        "api-key",
        "Ocp-Apim-Subscription-Key",
        DevConduitHeaderNames.UpstreamAuthorization,
        DevConduitHeaderNames.LocalSecret
    ];

    /// <summary>
    /// Networks (CIDR notation) that outbound calls may never connect to. The defaults cover
    /// the cloud instance metadata service (169.254.169.254, which hands out managed identity
    /// tokens), the Azure WireServer and IPv6 link-local. As with <see cref="SecretHeaderNames"/>,
    /// the configuration binder appends configured entries to these defaults, so configuration
    /// can add networks but never remove the defaults; duplicates are harmless.
    /// </summary>
    public string[] BlockedEgressNetworks { get; set; } =
    [
        "169.254.0.0/16",
        "168.63.129.16/32",
        "fe80::/10"
    ];

    /// <summary>When false, per-request TLS validation opt-out is refused.</summary>
    public bool AllowInsecureTlsOptIn { get; set; } = true;

    /// <summary>Entra group object ids allowed to use the tool. Empty allows any authenticated tenant user.</summary>
    public string[] AllowedGroupIds { get; set; } = [];

    /// <summary>Entra user object ids allowed to use the tool. Empty allows any authenticated tenant user.</summary>
    public string[] AllowedUserOids { get; set; } = [];

    public string ResolveDataRoot()
    {
        if (!string.IsNullOrWhiteSpace(DataRoot))
        {
            return Environment.ExpandEnvironmentVariables(DataRoot);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.Combine(AppContext.BaseDirectory, "data");
        }

        return Path.Combine(localAppData, "Proxylayer", "Dev.Conduit");
    }
}
