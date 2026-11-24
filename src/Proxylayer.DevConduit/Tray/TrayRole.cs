using System.Runtime.Versioning;
using Proxylayer.DevConduit.Hosting;

namespace Proxylayer.DevConduit.Tray;

/// <summary>Which job the tray does on this machine.</summary>
public enum TrayRole
{
    /// <summary>No Dev.Conduit service is installed: the tray hosts local mode in-process.</summary>
    Workstation,

    /// <summary>The Dev.Conduit service is installed: the tray manages it.</summary>
    Server
}

[SupportedOSPlatform("windows")]
public static class TrayRoles
{
    /// <summary>Server when the service is installed, otherwise workstation. Read each poll, so installing the service switches it.</summary>
    public static TrayRole Detect() => ServiceProbe.IsInstalled() ? TrayRole.Server : TrayRole.Workstation;

    public static string Describe(TrayRole role) => role == TrayRole.Server ? "Server role" : "Workstation role";
}
