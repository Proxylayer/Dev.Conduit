using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// Settings bound from "DevConduit:Local": the workstation knobs. They apply only when
/// <see cref="DevConduitOptions.Mode"/> is <see cref="DevConduitMode.Local"/>. The remote
/// instances local mode relays to, and the identity it signs in with for each, live under
/// "DevConduit:Remotes" and are read by <see cref="RoutingConfigurationParser"/>.
/// </summary>
public sealed class LocalModeOptions
{
    /// <summary>The address local mode binds unless configured otherwise. Loopback is mandatory.</summary>
    public const string DefaultBindAddress = "127.0.0.1";

    /// <summary>
    /// Storage key history is filed under in local mode. There is no Entra principal to
    /// take an object id from, and this value satisfies <c>UserStoragePaths.IsSafeSegment</c>.
    /// </summary>
    public const string PseudoUserId = "local";

    /// <summary>Port the local reverse proxy and console listen on.</summary>
    public int Port { get; set; } = 8092;

    /// <summary>Address to bind. Anything other than a loopback address refuses to start.</summary>
    public string BindAddress { get; set; } = DefaultBindAddress;

    /// <summary>When set, inbound proxy traffic must carry this value in X-DevConduit-Local-Secret.</summary>
    public string SharedSecret { get; set; } = string.Empty;

    /// <summary>Whether the raw HTTP_PROXY listener runs. Never runs in server mode.</summary>
    public bool ForwardProxyEnabled { get; set; } = true;

    /// <summary>Port the forward proxy listens on, separate from <see cref="Port"/>.</summary>
    public int ForwardProxyPort { get; set; } = 8093;

    /// <summary>
    /// Whether local mode captures the traffic it relays. Off by default: the remote instance
    /// already holds the authoritative record, and capturing twice stores every body twice.
    /// </summary>
    public bool CaptureEnabled { get; set; }

    /// <summary>Directory holding the encrypted MSAL token cache. Empty means %LOCALAPPDATA%\Proxylayer\Dev.Conduit\msal.</summary>
    public string CacheDirectory { get; set; } = string.Empty;

    /// <summary>When false, sign-in never opens a browser and goes straight to device code.</summary>
    public bool InteractiveFallback { get; set; } = true;

    /// <summary>The token cache directory, defaulting under the local application data folder.</summary>
    public string ResolveCacheDirectory()
    {
        if (!string.IsNullOrWhiteSpace(CacheDirectory))
        {
            return Environment.ExpandEnvironmentVariables(CacheDirectory);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = AppContext.BaseDirectory;
        }

        return Path.Combine(localAppData, "Proxylayer", "Dev.Conduit", "msal");
    }

    /// <summary>The Kestrel listen URL for local mode.</summary>
    public string ResolveBindUrl() => $"http://{BindAddress.Trim()}:{Port}";

    /// <summary>The bind address, or null with a reason when it is not a loopback address.</summary>
    public bool TryResolveBindAddress([NotNullWhen(true)] out IPAddress? address, out string error)
    {
        address = null;
        error = string.Empty;

        var value = BindAddress?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            value = DefaultBindAddress;
        }

        if (string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            address = IPAddress.Loopback;
            return true;
        }

        if (!IPAddress.TryParse(value, out var parsed))
        {
            error = $"DevConduit:Local:BindAddress ('{BindAddress}') is not an IP address. Local mode binds loopback only.";
            return false;
        }

        if (!IPAddress.IsLoopback(parsed))
        {
            error = $"DevConduit:Local:BindAddress ('{BindAddress}') is not a loopback address.";
            return false;
        }

        address = parsed;
        return true;
    }

    /// <summary>
    /// Configuration problems that make local mode unsafe or unable to work. A non-empty
    /// result stops the host before anything binds.
    /// </summary>
    public IReadOnlyList<string> FatalProblems()
    {
        var problems = new List<string>();

        if (!TryResolveBindAddress(out _, out var bindError))
        {
            problems.Add(
                bindError
                + " Local mode requires no inbound sign-in, so loopback is the boundary that keeps the "
                + "developer's token from being usable by anything off this machine.");
        }

        if (Port is < 1 or > 65535)
        {
            problems.Add($"DevConduit:Local:Port ({Port}) is not a usable TCP port.");
        }

        if (ForwardProxyEnabled && ForwardProxyPort is < 1 or > 65535)
        {
            problems.Add($"DevConduit:Local:ForwardProxyPort ({ForwardProxyPort}) is not a usable TCP port.");
        }

        if (ForwardProxyEnabled && ForwardProxyPort == Port)
        {
            problems.Add(
                $"DevConduit:Local:ForwardProxyPort and DevConduit:Local:Port are both {Port}. "
                + "The reverse proxy and the forward proxy are separate listeners and need separate ports.");
        }

        return problems;
    }
}
