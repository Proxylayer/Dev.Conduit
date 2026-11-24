using System.ComponentModel;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace Proxylayer.DevConduit.Hosting;

/// <summary>
/// Reads whether the Dev.Conduit service is installed and what state it is in. Querying a
/// service's status needs no administrator rights, so the unelevated tray polls through this.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceProbe
{
    /// <summary>The service's status, or null when it is not installed or cannot be queried.</summary>
    public static ServiceControllerStatus? Status()
    {
        using var controller = new ServiceController(DevConduitServiceHost.ServiceName);
        try
        {
            return controller.Status;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    public static bool IsInstalled() => Status() is not null;
}
