namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// The data root when <c>DevConduit:DataRoot</c> names none. A server's history, Data Protection
/// keys and certificates belong to the machine, so in server mode, when running as the service
/// or while the <c>Dev.Conduit</c> service is installed, the default is
/// <c>%ProgramData%\Proxylayer\Dev.Conduit</c>: a console run on the server (<c>export</c>, <c>certs</c>)
/// then finds what the service wrote. Everything else keeps <c>%LOCALAPPDATA%\Proxylayer\Dev.Conduit</c>,
/// the default <see cref="DevConduitOptions.ResolveDataRoot"/> falls back to. An explicit
/// <c>DevConduit:DataRoot</c> always wins.
/// </summary>
public static class DataRootDefault
{
    public const string Key = "DevConduit:DataRoot";

    /// <summary>The machine's data root, unexpanded as a setting would hold it.</summary>
    public const string MachineDataRoot = @"%ProgramData%\Proxylayer\Dev.Conduit";

    /// <summary>True when the machine folder is the default data root.</summary>
    public static bool UsesMachineFolder(DevConduitMode mode, bool runningAsService, bool serviceInstalled) =>
        mode == DevConduitMode.Server && (runningAsService || serviceInstalled);

    /// <summary>
    /// Adds <see cref="MachineDataRoot"/> as the data root of <paramref name="configuration"/> when
    /// it names none and <see cref="UsesMachineFolder"/> holds. <paramref name="serviceInstalled"/>
    /// is asked only when the answer depends on it. True when the default was added.
    /// </summary>
    public static bool Apply(ConfigurationManager configuration, DevConduitMode mode, bool runningAsService, Func<bool> serviceInstalled)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(serviceInstalled);

        if (!string.IsNullOrWhiteSpace(configuration[Key]))
        {
            return false;
        }

        // The probe runs only for a server-mode console run, the one case it decides.
        var installed = mode == DevConduitMode.Server && !runningAsService && serviceInstalled();
        if (!UsesMachineFolder(mode, runningAsService, installed))
        {
            return false;
        }

        configuration.AddInMemoryCollection([new KeyValuePair<string, string?>(Key, MachineDataRoot)]);
        return true;
    }

    /// <summary>
    /// The data root a launch would use, for display: the configured one, else the machine
    /// folder when <paramref name="usesMachineFolder"/>, else the per-user default. Expanded.
    /// </summary>
    public static string Resolve(string? configured, bool usesMachineFolder) =>
        new DevConduitOptions
        {
            DataRoot = string.IsNullOrWhiteSpace(configured) && usesMachineFolder ? MachineDataRoot : configured ?? string.Empty
        }.ResolveDataRoot();
}
