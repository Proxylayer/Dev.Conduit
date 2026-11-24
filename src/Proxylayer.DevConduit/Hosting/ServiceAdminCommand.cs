using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Hosting;

/// <summary>
/// The elevated helper, <c>Dev.Conduit.exe service-admin &lt;action&gt; [arguments]</c>. The
/// tray runs unelevated and starts this with the <c>runas</c> verb for anything that needs an
/// administrator: controlling, installing and uninstalling the service, and writing the
/// machine settings file. Every target is fixed: the service is always
/// <see cref="DevConduitServiceHost.ServiceName"/>, the settings file always the machine file
/// under <c>%ProgramData%</c>, and a file argument must name a transfer file of the matching
/// kind: a settings change for <c>save</c>, a copied <c>.conduit</c> for <c>import</c>. The
/// certificate actions are <c>export &lt;out.conduit&gt; [address]</c>, <c>import &lt;transfer&gt;</c>,
/// <c>seed &lt;transfer&gt;</c> (a server copying the Entra ids and routes from a <c>.conduit</c>),
/// <c>trust &lt;hostname&gt;</c>, <c>forget &lt;hostname&gt;</c>, <c>revoke &lt;serial&gt;</c>,
/// <c>revoke --enrollment &lt;id&gt;</c> (both revokes cover a whole enrollment) and <c>untrust</c>,
/// which removes the server's own certificates from Windows trust. <c>update &lt;transfer&gt; [pid]</c>
/// installs a verified update package (<see cref="UpdateAdminAction"/>).
/// While the Dev.Conduit MSI is installed, in any role, install refuses with
/// <see cref="ServiceAdminExitCode.ManagedByInstaller"/>; uninstall refuses only while it is
/// installed in its Server role, the one that installs the service.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class ServiceAdminCommand
{
    public const string StartAction = "start";
    public const string StopAction = "stop";
    public const string RestartAction = "restart";
    public const string InstallAction = "install";
    public const string UninstallAction = "uninstall";
    public const string SaveAction = "save";
    public const string ExportAction = "export";
    public const string ImportAction = "import";
    public const string TrustAction = "trust";
    public const string RevokeAction = "revoke";
    public const string ForgetAction = "forget";
    public const string SeedAction = "seed";
    public const string UntrustAction = "untrust";
    public const string UpdateAction = UpdateAdminAction.Action;

    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ChildTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Runs one action and returns its <see cref="ServiceAdminExitCode"/>.</summary>
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            return (int)ServiceAdminExitCode.InvalidArguments;
        }

        if (!IsElevated())
        {
            return (int)ServiceAdminExitCode.NotElevated;
        }

        try
        {
            return (int)(args[0].ToLowerInvariant() switch
            {
                StartAction when args.Length == 1 => Control(StartService),
                StopAction when args.Length == 1 => Control(StopService),
                RestartAction when args.Length == 1 => Control(controller =>
                    StopService(controller) is var stopped && stopped != ServiceAdminExitCode.Success ? stopped : StartService(controller)),
                InstallAction when args.Length == 2 => Install(args[1]),
                UninstallAction when args.Length == 1 => Uninstall(),
                SaveAction when args.Length is 2 or 3 => Save(args[1], args.Length == 3 ? args[2] : null),
                ExportAction when args.Length is 2 or 3 => Export(args[1], args.Length == 3 ? args[2] : null),
                ImportAction when args.Length == 2 => Import(args[1]),
                SeedAction when args.Length == 2 => Seed(args[1]),
                UntrustAction when args.Length == 1 => CertsCommand.Untrust([], SettingsFiles.ForThisComputer(), TextWriter.Null),
                TrustAction when args.Length == 2 => Trust(args[1]),
                RevokeAction when args.Length == 2 => Revoke(args[1]),
                RevokeAction when args.Length == 3 && string.Equals(args[1], CertsCommand.EnrollmentOption, StringComparison.Ordinal) =>
                    RevokeEnrollment(args[2]),
                ForgetAction when args.Length == 2 => Forget(args[1]),
                UpdateAction when args.Length is 2 or 3 => UpdateAdminAction.Run(args[1..]),
                _ => ServiceAdminExitCode.InvalidArguments
            });
        }
        catch (Exception)
        {
            return (int)ServiceAdminExitCode.UnexpectedError;
        }
    }

    /// <summary>
    /// An environment name the helper passes on: letters, digits, dot, dash and underscore,
    /// so it reaches the install command line and the service's registry environment intact.
    /// </summary>
    public static bool IsAcceptedEnvironmentName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 64 && EnvironmentName().IsMatch(name);

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static ServiceAdminExitCode Control(Func<ServiceController, ServiceAdminExitCode> action)
    {
        if (!ServiceProbe.IsInstalled())
        {
            return ServiceAdminExitCode.ServiceNotInstalled;
        }

        using var controller = new ServiceController(DevConduitServiceHost.ServiceName);
        try
        {
            return action(controller);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return ServiceAdminExitCode.ServiceControlFailed;
        }
    }

    private static ServiceAdminExitCode StartService(ServiceController controller)
    {
        controller.Refresh();
        if (controller.Status == ServiceControllerStatus.Running)
        {
            return ServiceAdminExitCode.Success;
        }

        if (controller.Status == ServiceControllerStatus.StopPending
            && WaitFor(controller, ServiceControllerStatus.Stopped) is var stopped
            && stopped != ServiceAdminExitCode.Success)
        {
            return stopped;
        }

        if (controller.Status != ServiceControllerStatus.StartPending)
        {
            controller.Start();
        }

        return WaitFor(controller, ServiceControllerStatus.Running);
    }

    private static ServiceAdminExitCode StopService(ServiceController controller)
    {
        controller.Refresh();
        if (controller.Status == ServiceControllerStatus.Stopped)
        {
            return ServiceAdminExitCode.Success;
        }

        if (controller.Status != ServiceControllerStatus.StopPending)
        {
            controller.Stop();
        }

        return WaitFor(controller, ServiceControllerStatus.Stopped);
    }

    /// <summary>
    /// Waits for the service to reach <paramref name="desired"/>. A service that fails to start
    /// falls back to Stopped rather than reaching Running, which is reported at once instead
    /// of after the whole timeout.
    /// </summary>
    private static ServiceAdminExitCode WaitFor(ServiceController controller, ServiceControllerStatus desired)
    {
        var deadline = DateTime.UtcNow + ServiceTimeout;
        var sawPending = false;

        while (DateTime.UtcNow < deadline)
        {
            controller.Refresh();
            var status = controller.Status;

            if (status == desired)
            {
                return ServiceAdminExitCode.Success;
            }

            if (status is ServiceControllerStatus.StartPending or ServiceControllerStatus.StopPending)
            {
                sawPending = true;
            }
            else if (sawPending)
            {
                return ServiceAdminExitCode.ServiceControlFailed;
            }

            Thread.Sleep(PollInterval);
        }

        return ServiceAdminExitCode.ServiceTimedOut;
    }

    /// <summary>Installs through the exe's own Topshelf <c>install</c>, in a child process with no window.</summary>
    private static ServiceAdminExitCode Install(string environmentName)
    {
        if (!IsAcceptedEnvironmentName(environmentName))
        {
            return ServiceAdminExitCode.InvalidArguments;
        }

        if (InstallerOwnership.Detect().OwnsServiceInstall)
        {
            return ServiceAdminExitCode.ManagedByInstaller;
        }

        if (ServiceProbe.IsInstalled())
        {
            return ServiceAdminExitCode.ServiceAlreadyInstalled;
        }

        var exitCode = RunSelf("install", $"-{DevConduitServiceHost.EnvironmentArgument}:{environmentName}");
        return exitCode == 0 && ServiceProbe.IsInstalled() ? ServiceAdminExitCode.Success : ServiceAdminExitCode.InstallFailed;
    }

    /// <summary>
    /// Refused only while the MSI is installed in its Server role. With its Workstation role,
    /// or no saved role, a service present was installed some other way and is removed here.
    /// </summary>
    private static ServiceAdminExitCode Uninstall()
    {
        if (InstallerOwnership.Detect().OwnsServiceUninstall)
        {
            return ServiceAdminExitCode.ManagedByInstaller;
        }

        if (!ServiceProbe.IsInstalled())
        {
            return ServiceAdminExitCode.ServiceNotInstalled;
        }

        var stopped = Control(StopService);
        if (stopped != ServiceAdminExitCode.Success)
        {
            return stopped;
        }

        return RunSelf("uninstall") == 0 ? ServiceAdminExitCode.Success : ServiceAdminExitCode.UninstallFailed;
    }

    /// <summary>
    /// Applies a transfer file to the machine settings file, with the file's own access list,
    /// and optionally sets the service's environment name. On an MSI installation the name is
    /// also written over the environment the MSI saved, so its next upgrade or repair keeps it.
    /// The transfer file is deleted whatever the outcome, once its name has been checked.
    /// </summary>
    private static ServiceAdminExitCode Save(string transferPath, string? environmentName)
    {
        try
        {
            if (environmentName is not null && !IsAcceptedEnvironmentName(environmentName))
            {
                return ServiceAdminExitCode.InvalidArguments;
            }

            if (!MachineSettingsTransfer.TryRead(transferPath, ManagedSettingKeys.Machine, out var changes, out _))
            {
                return ServiceAdminExitCode.SettingsRejected;
            }

            try
            {
                new SettingsFileWriter(SettingsFiles.ForThisComputer().MachinePath, MachineSettingsAccess.BuildSecurity)
                    .Apply(changes);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return ServiceAdminExitCode.SettingsWriteFailed;
            }

            if (environmentName is not null && ServiceProbe.IsInstalled())
            {
                ServiceEnvironment.Set(DevConduitServiceHost.ServiceName, ServiceEnvironment.EnvironmentVariable, environmentName);
                InstallerOwnership.Detect().UpdateSavedEnvironment(environmentName);
            }

            return ServiceAdminExitCode.Success;
        }
        finally
        {
            MachineSettingsTransfer.Delete(transferPath);
        }
    }

    /// <summary>
    /// Writes a .conduit for a client. The output must be a fully qualified .conduit path
    /// in an existing folder, and the address an IP address or host name.
    /// </summary>
    private static ServiceAdminExitCode Export(string outputPath, string? address)
    {
        if (!Path.IsPathFullyQualified(outputPath)
            || !outputPath.EndsWith(ProxyConfigDocument.FileExtension, StringComparison.OrdinalIgnoreCase)
            || (address is not null && !DnsHostname.IsAddressOrHostname(address)))
        {
            return ServiceAdminExitCode.InvalidArguments;
        }

        return ExportCommand.Export(
            outputPath, address, [], requireQualifiedOutput: true, SettingsFiles.ForThisComputer(), loggers: null, TextWriter.Null);
    }

    /// <summary>Imports a .conduit the caller copied to a transfer file, then deletes the transfer file.</summary>
    private static ServiceAdminExitCode Import(string transferPath)
    {
        try
        {
            if (!ProxyConfigTransfer.TryRead(transferPath, out var content, out _))
            {
                return ServiceAdminExitCode.InvalidArguments;
            }

            if (!ProxyConfigValidator.TryValidate(content, TimeProvider.System.GetUtcNow(), out var config, out _))
            {
                return ServiceAdminExitCode.ImportRejected;
            }

            return ImportCommand.Apply(config, ProxyConfigImportTargets.ForThisComputer(), TextWriter.Null);
        }
        finally
        {
            ProxyConfigTransfer.Delete(transferPath);
        }
    }

    /// <summary>
    /// Seeds this server's machine settings from a .conduit the caller copied to a transfer file,
    /// checked as strictly as an import, then deletes the transfer file. Only the Entra ids and
    /// the routes are written (<see cref="ManagedSettingKeys.Seed"/>).
    /// </summary>
    private static ServiceAdminExitCode Seed(string transferPath)
    {
        try
        {
            if (!ProxyConfigTransfer.TryRead(transferPath, out var content, out _))
            {
                return ServiceAdminExitCode.InvalidArguments;
            }

            return ProxyConfigValidator.TryValidate(content, TimeProvider.System.GetUtcNow(), out var config, out _)
                ? ImportCommand.Seed(config, SettingsFiles.ForThisComputer().MachinePath, protect: true, TextWriter.Null)
                : ServiceAdminExitCode.ImportRejected;
        }
        finally
        {
            ProxyConfigTransfer.Delete(transferPath);
        }
    }

    /// <summary>
    /// Refreshes an imported server's machine pins and LocalMachine\Root from the server itself
    /// (<see cref="MachineTrustRefresh"/>): <c>/cert</c> called with the machine identity and
    /// validated against the machine pins, never the user's layer, every certificate checked
    /// against the server certificate rules for the host. Then renews the machine identity when
    /// it is due; a failed renewal keeps the current one and ends with
    /// <see cref="ServiceAdminExitCode.IdentityRenewalFailed"/>, the trust update standing.
    /// </summary>
    private static ServiceAdminExitCode Trust(string hostname)
    {
        if (HostnameArgument(hostname) is not { } accepted)
        {
            return ServiceAdminExitCode.InvalidArguments;
        }

        try
        {
            var result = new MachineTrustRefresh(ProxyConfigImportTargets.ForThisComputer(), TimeProvider.System)
                .RefreshAsync(accepted, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return !result.Found
                ? ServiceAdminExitCode.NotFound
                : result.Identity == MachineIdentityRenewal.Failed
                    ? ServiceAdminExitCode.IdentityRenewalFailed
                    : ServiceAdminExitCode.Success;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Security.Cryptography.CryptographicException
                                              or IOException or UnauthorizedAccessException or HttpRequestException
                                              or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            return ServiceAdminExitCode.TrustFailed;
        }
    }

    /// <summary>
    /// A host name the helper accepts for <c>trust</c> and <c>forget</c>: a DNS host name as an
    /// import stores it, already lower case, without surrounding space or a trailing dot.
    /// </summary>
    public static bool IsAcceptedHostname(string? hostname) =>
        hostname is not null
        && DnsHostname.IsValid(hostname)
        && string.Equals(hostname, DnsHostname.Normalize(hostname), StringComparison.Ordinal);

    /// <summary>
    /// The host name argument of <c>trust</c> and <c>forget</c>, which accept the same input:
    /// lower-cased, then accepted by <see cref="IsAcceptedHostname"/>; null otherwise.
    /// </summary>
    public static string? HostnameArgument(string? hostname)
    {
        var lowered = hostname?.ToLowerInvariant();
        return IsAcceptedHostname(lowered) ? lowered : null;
    }

    /// <summary>
    /// Removes an imported server: its remote and routes, its machine-layer client folder, hosts
    /// line, the certificates this app added to Windows trust for it, and its import record. The
    /// user layers are the users' own; the tray removes the current user's afterwards.
    /// </summary>
    private static ServiceAdminExitCode Forget(string hostname)
    {
        if (HostnameArgument(hostname) is not { } accepted)
        {
            return ServiceAdminExitCode.InvalidArguments;
        }

        hostname = accepted;

        try
        {
            var removal = new ImportedServerRemover(ProxyConfigImportTargets.ForThisComputer(), TimeProvider.System).Remove(hostname);
            return removal.Found ? ServiceAdminExitCode.Success : ServiceAdminExitCode.NotFound;
        }
        catch (InvalidOperationException)
        {
            return ServiceAdminExitCode.SettingsRejected;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                              or System.Security.Cryptography.CryptographicException)
        {
            return ServiceAdminExitCode.ForgetFailed;
        }
    }

    /// <summary>Revokes the enrollment a serial belongs to: every certificate in it.</summary>
    private static ServiceAdminExitCode Revoke(string serial) =>
        CertsCommand.IsAcceptedSerial(serial)
            ? CertsCommand.Revoke(serial, [], SettingsFiles.ForThisComputer(), TextWriter.Null)
            : ServiceAdminExitCode.InvalidArguments;

    private static ServiceAdminExitCode RevokeEnrollment(string enrollment) =>
        ClientEnrollmentId.IsValid(enrollment)
            ? CertsCommand.RevokeEnrollment(enrollment, [], SettingsFiles.ForThisComputer(), TextWriter.Null)
            : ServiceAdminExitCode.InvalidArguments;

    private static int RunSelf(params string[] arguments)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("The process path is unknown."))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("The child process did not start.");

        // Drained so a chatty child can never block on a full pipe.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(ChildTimeout))
        {
            process.Kill(entireProcessTree: true);
            return -1;
        }

        Task.WaitAll(output, error);
        return process.ExitCode;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentName();
}
