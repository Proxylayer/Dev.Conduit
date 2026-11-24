using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Hosting;

/// <summary>
/// The configuration the server's command-line verbs (<c>export</c>, <c>certs</c>) read: the
/// same layers the service reads, so they find the same data root, host name and Entra ids.
/// That is <c>appsettings.json</c> and <c>appsettings.{Environment}.json</c> from the exe's
/// folder, the machine settings file, the environment, and finally any <c>--Key value</c>
/// arguments. The environment name is the process's own, else the one the service runs
/// with, else the one the MSI recorded, else Production. The data root follows
/// <see cref="DataRootDefault"/>: on a server whose service is installed it defaults to the
/// service's, <c>%ProgramData%\Proxylayer\Dev.Conduit</c>, so an export finds the service's certificates,
/// and the host name follows <see cref="ServerHostnameDefault"/>, so it names the same server the service serves.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CommandConfiguration
{
    public static bool TryBuild(
        string[] configurationArguments,
        SettingsFiles settingsFiles,
        [NotNullWhen(true)] out ConfigurationManager? configuration,
        out string error)
    {
        var environmentName = ResolveEnvironmentName();
        var manager = new ConfigurationManager();
        manager.SetBasePath(AppContext.BaseDirectory);
        manager.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
        manager.AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: false);
        manager.AddEnvironmentVariables();
        manager.AddCommandLine(configurationArguments);

        if (!settingsFiles.TryAddTo(manager, environmentName, runningAsService: true, out error))
        {
            configuration = null;
            return false;
        }

        // An unreadable mode is reported by the verb itself; until then the default, server, applies.
        var mode = DevConduitModeSelection.TryResolve(manager, configurationArguments, out var resolved, out _) ? resolved : DevConduitMode.Server;
        DataRootDefault.Apply(manager, mode, runningAsService: false, ServiceProbe.IsInstalled);
        ServerHostnameDefault.Apply(manager, mode, runningAsService: false, ServiceProbe.IsInstalled, ServerHostnameDefault.ComputerName);

        configuration = manager;
        return true;
    }

    public static string ResolveEnvironmentName()
    {
        var fromProcess = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (!string.IsNullOrWhiteSpace(fromProcess))
        {
            return fromProcess.Trim();
        }

        try
        {
            var fromService = ServiceEnvironment.Get(DevConduitServiceHost.ServiceName, ServiceEnvironment.EnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromService))
            {
                return fromService.Trim();
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unreadable: fall through to the MSI's record.
        }

        return InstallerOwnership.Detect().Environment ?? Environments.Production;
    }
}
