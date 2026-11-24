namespace Proxylayer.DevConduit.Hosting;

/// <summary>
/// The exit codes of the elevated helper, <c>service-admin</c>. The helper runs elevated in a
/// window nobody sees, so its exit code is the whole of what it reports; the tray turns each
/// into the message from <see cref="ServiceAdminExitCodes.Describe"/>.
/// </summary>
public enum ServiceAdminExitCode
{
    Success = 0,
    UnexpectedError = 1,
    InvalidArguments = 2,
    NotElevated = 3,
    ServiceNotInstalled = 4,
    ServiceAlreadyInstalled = 5,
    ServiceTimedOut = 6,
    ServiceControlFailed = 7,
    SettingsRejected = 8,
    SettingsWriteFailed = 9,
    InstallFailed = 10,
    UninstallFailed = 11,
    ManagedByInstaller = 12,
    ExportRefused = 13,
    ExportFailed = 14,
    ImportRejected = 15,
    ImportFailed = 16,
    TrustFailed = 17,
    NotFound = 18,
    ForgetFailed = 19,
    IdentityRenewalFailed = 20,
    UntrustFailed = 21,
    UpdateRefused = 22,
    UpdateFailed = 23
}

public static class ServiceAdminExitCodes
{
    /// <summary>A sentence for the person who asked for the action.</summary>
    public static string Describe(int exitCode) =>
        Enum.IsDefined(typeof(ServiceAdminExitCode), exitCode)
            ? Describe((ServiceAdminExitCode)exitCode)
            : $"The administrative helper ended with an unknown exit code ({exitCode}).";

    public static string Describe(ServiceAdminExitCode exitCode) => exitCode switch
    {
        ServiceAdminExitCode.Success => "Done.",
        ServiceAdminExitCode.InvalidArguments => "The administrative helper was started with arguments it does not accept.",
        ServiceAdminExitCode.NotElevated => "The administrative helper did not run with administrator rights.",
        ServiceAdminExitCode.ServiceNotInstalled => $"The {DevConduitServiceHost.ServiceName} service is not installed.",
        ServiceAdminExitCode.ServiceAlreadyInstalled => $"The {DevConduitServiceHost.ServiceName} service is already installed.",
        ServiceAdminExitCode.ServiceTimedOut =>
            "The service did not reach the requested state in time. It may still be starting or stopping; check the event log.",
        ServiceAdminExitCode.ServiceControlFailed =>
            $"The service could not be started or stopped. The Application event log, source {ServiceEventLog.SourceName}, says why.",
        ServiceAdminExitCode.SettingsRejected =>
            "The machine settings change was refused: it was not a settings change the setup window makes.",
        ServiceAdminExitCode.SettingsWriteFailed =>
            "The machine settings file could not be written. If it was created by another account, delete it and save again.",
        ServiceAdminExitCode.InstallFailed =>
            "The service could not be installed. Run Dev.Conduit.exe install from an elevated prompt to see why.",
        ServiceAdminExitCode.UninstallFailed =>
            "The service could not be uninstalled. Run Dev.Conduit.exe uninstall from an elevated prompt to see why.",
        ServiceAdminExitCode.ManagedByInstaller =>
            $"The {DevConduitServiceHost.ServiceName} service belongs to the Dev.Conduit MSI. Install, change or remove it with Settings > Apps (or msiexec), not here.",
        ServiceAdminExitCode.ExportRefused =>
            "The export was refused: this machine must run in server mode with DevConduit:Server:Hostname, the AzureAd tenant and client ids, and a client address (DevConduit:Server:ClientAddress or --address), and without Kestrel:Endpoints.",
        ServiceAdminExitCode.ExportFailed =>
            "The .conduit could not be written. Check the output folder and the certificate folder under the data root.",
        ServiceAdminExitCode.ImportRejected =>
            "The .conduit file was refused: it is not a valid file from a Dev.Conduit server, or its certificates have expired. Run Dev.Conduit.exe import <file> to see why.",
        ServiceAdminExitCode.ImportFailed =>
            "The .conduit could not be applied. Run Dev.Conduit.exe import <file> --machine from an elevated prompt to see why.",
        ServiceAdminExitCode.TrustFailed =>
            @"The server's certificates could not be added to Windows trust (LocalMachine\Root): the server could not be reached, refused this machine's client certificate (revoked or expired: import a new .conduit), or the store could not be changed.",
        ServiceAdminExitCode.NotFound =>
            "Nothing was found to act on: no imported server by that name, or no client certificate with that serial number.",
        ServiceAdminExitCode.IdentityRenewalFailed =>
            @"The server's certificates are up to date in Windows trust (LocalMachine\Root), but this computer's client certificate could not be renewed; the current one is kept and works until it expires. Try again later, and import a new .conduit if it keeps failing.",
        ServiceAdminExitCode.ForgetFailed =>
            @"The server could not be removed completely: its hosts line, its folder under %ProgramData%\Proxylayer\Dev.Conduit\client or its settings may remain. Try again.",
        ServiceAdminExitCode.UntrustFailed =>
            @"This server's certificates could not be removed from Windows trust (LocalMachine\Root). Run Dev.Conduit.exe certs untrust from an elevated prompt to see why.",
        ServiceAdminExitCode.UpdateRefused =>
            @"The update was refused: the package failed a check (checksum, signature, or that it is a newer Dev.Conduit), or this is not an installed build. %ProgramData%\Proxylayer\Dev.Conduit\updates\update-helper.log says why.",
        ServiceAdminExitCode.UpdateFailed =>
            @"The update could not be started. %ProgramData%\Proxylayer\Dev.Conduit\updates\update-helper.log says why.",
        _ => "The administrative helper failed unexpectedly."
    };
}
