using System.Diagnostics;
using System.Runtime.Versioning;
using Topshelf;

namespace Proxylayer.DevConduit.Hosting;

/// <summary>
/// Runs the application as a Windows service through Topshelf, which handles only the
/// service lifecycle: install, uninstall, start, stop, help, and running under the service
/// control manager. The web application is built exactly as for a console run, except that
/// it receives no command-line arguments: the service control manager starts the exe with
/// Topshelf's own <c>-displayname</c> and <c>-servicename</c> arguments, which the
/// configuration command-line provider would reject, so a service is configured from the
/// appsettings files and the environment alone. While the Dev.Conduit MSI is installed it
/// owns the service, and <c>install</c> and <c>uninstall</c> are refused before Topshelf
/// reaches the service control manager.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DevConduitServiceHost
{
    public const string ServiceName = "Dev.Conduit";

    public const string DisplayName = "Dev.Conduit";

    public const string Description =
        "Developer HTTP proxy and request console, server mode. Signs callers in with Microsoft Entra ID and forwards their requests.";

    /// <summary>The custom install argument, <c>-environment:&lt;name&gt;</c>.</summary>
    public const string EnvironmentArgument = "environment";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    // Longer than the host's own 30 second shutdown timeout, so a slow drain of in-flight
    // requests ends inside the web host rather than being cut off by the service host.
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(45);

    public static int Run(string[] args)
    {
        if (LeavesToInstaller(args, InstallerOwnership.Detect))
        {
            Console.Error.WriteLine(ServiceAdminExitCodes.Describe(ServiceAdminExitCode.ManagedByInstaller));
            return (int)ServiceAdminExitCode.ManagedByInstaller;
        }

        string? environmentName = null;
        var fatal = false;

        var exitCode = HostFactory.Run(host =>
        {
            host.Service<DevConduitService>(service =>
            {
                service.ConstructUsing(() =>
                {
                    if (!DevConduitApplication.TryBuild([], runningAsService: true, out var app, out var fatalMessage))
                    {
                        ServiceEventLog.WriteFatal(fatalMessage);
                        fatal = true;
                        throw new InvalidOperationException(fatalMessage);
                    }

                    return new DevConduitService(app);
                });
                service.WhenStarted((instance, hostControl) => instance.Start(hostControl));
                service.WhenStopped((instance, _) => instance.Stop());
            });

            host.SetServiceName(ServiceName);
            host.SetDisplayName(DisplayName);
            host.SetDescription(Description);
            host.StartAutomaticallyDelayed();

            // The default account. Topshelf's own -username/-password, --localservice,
            // --networkservice and --interactive install arguments are applied after this
            // and replace it.
            host.RunAsLocalSystem();

            host.SetStartTimeout(StartTimeout);
            host.SetStopTimeout(StopTimeout);

            host.EnableServiceRecovery(recovery =>
            {
                recovery.RestartService(1);
                recovery.SetResetPeriod(1);
            });

            host.AddCommandLineDefinition(EnvironmentArgument, value => environmentName = ValidateEnvironmentName(value));
            host.AfterInstall(settings => AfterInstall(settings.ServiceName, environmentName));
        });

        return fatal ? DevConduitApplication.FatalExitCode : (int)exitCode;
    }

    /// <summary>
    /// True when the command line's verb is the MSI's to carry out: <c>install</c> while the
    /// MSI is installed in any role, <c>uninstall</c> only while it is installed in its Server
    /// role (see <see cref="InstallerOwnership.OwnsServiceUninstall"/>). The installation is
    /// looked up only for those verbs, never for a start by the service control manager.
    /// </summary>
    public static bool LeavesToInstaller(string[] args, Func<InstallerOwnership> detect)
    {
        ArgumentNullException.ThrowIfNull(detect);
        if (!ServiceLaunch.ChangesServiceRegistration(args))
        {
            return false;
        }

        var installer = detect();
        return (HasVerb(args, "install") && installer.OwnsServiceInstall)
            || (HasVerb(args, "uninstall") && installer.OwnsServiceUninstall);
    }

    private static bool HasVerb(string[] args, string verb) =>
        args.Any(argument => string.Equals(argument, verb, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Rejects an environment name that could not be stored as one <c>NAME=value</c> entry.
    /// Throwing here stops the install before anything is written.
    /// </summary>
    public static string ValidateEnvironmentName(string? value)
    {
        var name = value?.Trim();
        if (string.IsNullOrEmpty(name) || name.Contains('=') || name.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"-{EnvironmentArgument} needs an environment name, for example -{EnvironmentArgument}:Uat.",
                nameof(value));
        }

        return name;
    }

    private static void AfterInstall(string serviceName, string? environmentName)
    {
        RegisterEventSource();

        if (environmentName is null)
        {
            Console.WriteLine(
                $"No -{EnvironmentArgument} was given, so the service reads {ServiceEnvironment.EnvironmentVariable} from " +
                "the machine environment as it was at boot, and is Production without it.");
            return;
        }

        // Topshelf names the registry key after the service, with $<instance> appended when
        // -instance is used, which is exactly the name the install settings report.
        ServiceEnvironment.Set(serviceName, ServiceEnvironment.EnvironmentVariable, environmentName);
        Console.WriteLine($"The service runs with {ServiceEnvironment.EnvironmentVariable}={environmentName}.");
    }

    /// <summary>
    /// Registers the event log source while the install runs elevated, so a service account
    /// without administrative rights can still write to it.
    /// </summary>
    private static void RegisterEventSource()
    {
        try
        {
            if (!EventLog.SourceExists(ServiceEventLog.SourceName))
            {
                EventLog.CreateEventSource(ServiceEventLog.SourceName, ServiceEventLog.LogName);
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"The event log source {ServiceEventLog.SourceName} could not be registered: {exception.Message}");
        }
    }
}
