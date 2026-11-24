using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Endpoints;
using Proxylayer.DevConduit.Services;
using Proxylayer.DevConduit.Updates;

namespace Proxylayer.DevConduit.Hosting;

/// <summary>
/// Builds the web application. The console run and the Windows service run share this one
/// path, including the checks that refuse to start, so both come up identically configured.
/// </summary>
public static class DevConduitApplication
{
    /// <summary>The process exit code when configuration stops the application from starting.</summary>
    public const int FatalExitCode = 1;

    /// <summary>Builds and runs the application in the console; the launch path used interactively.</summary>
    public static int RunConsole(string[] args)
    {
        if (!TryBuild(args, runningAsService: false, out var app, out var fatalMessage))
        {
            Console.Error.WriteLine(fatalMessage);
            return FatalExitCode;
        }

        app.Run();
        return 0;
    }

    /// <summary>
    /// Builds the application, or returns false with the message to report when configuration
    /// stops it from starting. The caller decides where the message goes and exits with
    /// <see cref="FatalExitCode"/>.
    /// </summary>
    public static bool TryBuild(
        string[] args,
        bool runningAsService,
        [NotNullWhen(true)] out WebApplication? app,
        out string fatalMessage) =>
        TryBuild(args, runningAsService, SettingsFiles.ForThisComputer(), logSink: null, out app, out fatalMessage);

    /// <summary>
    /// Builds the application with the given settings files layered over the appsettings
    /// files. <paramref name="logSink"/>, when given, replaces every other log provider: the
    /// tray hosts local mode without a console and shows this sink in its log window.
    /// </summary>
    public static bool TryBuild(
        string[] args,
        bool runningAsService,
        SettingsFiles settingsFiles,
        ILoggerProvider? logSink,
        [NotNullWhen(true)] out WebApplication? app,
        out string fatalMessage)
    {
        ArgumentNullException.ThrowIfNull(settingsFiles);

        app = null;
        fatalMessage = string.Empty;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = ResolveContentRoot(runningAsService)
        });

        // Above the appsettings files, below the environment and the command line. The service
        // reads the machine file only: the user file belongs to whoever is signed in.
        if (!settingsFiles.TryAddTo(builder.Configuration, builder.Environment.EnvironmentName, runningAsService, out var settingsError))
        {
            fatalMessage = settingsError;
            return false;
        }

        if (logSink is not null)
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logSink);
        }

        if (!DevConduitModeSelection.TryResolve(builder.Configuration, args, out var mode, out var modeError))
        {
            fatalMessage = modeError;
            return false;
        }

        if (runningAsService && mode == DevConduitMode.Local)
        {
            // Local mode signs in interactively to fill its token cache, and a service runs
            // in session 0 where no prompt can ever be shown.
            fatalMessage = "Local mode cannot run as a Windows service: it needs an interactive sign-in, which a service " +
                $"cannot show. Set {DevConduitModeSelection.ConfigurationKey} to server for the service, or run local mode " +
                "from a console with Dev.Conduit.exe --mode local.";
            return false;
        }

        if (runningAsService && OperatingSystem.IsWindows())
        {
            ServiceEventLog.Configure(builder.Logging);
        }

        // A server's data belongs to the machine, so the service and a console run beside it
        // share one data root unless DevConduit:DataRoot says otherwise.
        DataRootDefault.Apply(builder.Configuration, mode, runningAsService, () => OperatingSystem.IsWindows() && ServiceProbe.IsInstalled());

        // By the same rule a server names itself after the computer unless told otherwise, so
        // its HTTPS endpoint and certificate come up without configuration.
        var defaultHostname = ServerHostnameDefault.Apply(
            builder.Configuration, mode, runningAsService, () => OperatingSystem.IsWindows() && ServiceProbe.IsInstalled(), ServerHostnameDefault.ComputerName);

        builder.Services.Configure<DevConduitOptions>(builder.Configuration.GetSection(DevConduitOptions.SectionName));
        builder.Services.Configure<ApiScopeOptions>(builder.Configuration.GetSection(ApiScopeOptions.SectionName));
        var devConduit = builder.Configuration.GetSection(DevConduitOptions.SectionName).Get<DevConduitOptions>() ?? new DevConduitOptions();

        // Remotes and routes may each be a string or an object, which binding cannot express, so
        // they are parsed once here and shared: server mode reads the routes as its alias table.
        var routing = RoutingConfigurationParser.Parse(
            builder.Configuration.GetSection(DevConduitOptions.SectionName),
            builder.Configuration.GetSection("AzureAd"));
        builder.Services.AddSingleton(routing);

        ManagedHttpsEndpoint? managedHttps = null;
        if (mode == DevConduitMode.Local)
        {
            // Checked before anything binds: local mode runs without inbound sign-in, so a
            // misconfiguration here is the difference between a developer tool and an open relay.
            var problems = LocalModeStartup.Validate(devConduit, routing);
            if (problems.Count > 0)
            {
                // A retired key is the likeliest reason for a missing remote, so name each one here:
                // the usual startup warning comes too late, after the host would have been built.
                fatalMessage = ModeBanner.FatalMessage([.. problems, .. RemovedSettings.Warnings(builder.Configuration)]);
                return false;
            }

            LocalModeStartup.Register(builder, devConduit);
        }
        else
        {
            managedHttps = ServerModeStartup.Register(builder, devConduit);

            // Installs updates only when running as the service from the MSI's Server role; any
            // other server run logs that update checks are off.
            if (OperatingSystem.IsWindows())
            {
                ServiceUpdateWorker.Register(builder, runningAsService, settingsFiles);
            }
        }

        var dataProtection = DataProtectionSetup.Register(builder, devConduit);

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<UserStoragePaths>();
        builder.Services.AddSingleton<UserFileLocks>();
        builder.Services.AddSingleton<SecretHeaderPolicy>();
        builder.Services.AddSingleton<IUserStore, JsonUserStore>();
        builder.Services.AddSingleton<IRoutedTrafficStore, RoutedTrafficStore>();
        builder.Services.AddSingleton<StorageUsageReporter>();
        builder.Services.AddScoped<IUserContext, UserContext>();
        builder.Services.AddProxyHttpClients();
        builder.Services.AddSingleton<IProxyExecutor, ProxyExecutor>();

        builder.Services.AddSingleton<RouteAliasTable>();
        builder.Services.AddSingleton<RouteTargetResolver>();
        builder.Services.AddSingleton<RouteForwarder>();
        builder.Services.AddSingleton<RoutedTrafficRecorder>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<RoutedTrafficRecorder>());

        app = builder.Build();

        ModeBanner.Report(app, devConduit);
        RemovedSettings.Report(app);
        dataProtection.Report(app);
        managedHttps?.Report(app);
        if (defaultHostname is not null)
        {
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Dev.Conduit.Startup").LogInformation(
                "{Key} is not set, so the server uses this computer's name, {Hostname}.", ServerHostnameDefault.Key, defaultHostname);
        }

        // First of all, ahead of the notice below and of sign-in: a probe gets its answer even
        // while the Entra ids are missing and authentication cannot run at all.
        var unconfigured = mode == DevConduitMode.Server && StartupDiagnostics.IsUnconfigured(app);
        app.MapDevConduitHealth(configured: !unconfigured);

        if (mode == DevConduitMode.Server)
        {
            StartupDiagnostics.Report(app);

            if (unconfigured)
            {
                // Without a tenant and client id every sign-in attempt fails deep inside the OIDC
                // handler, so say what is missing instead of returning an opaque error page.
                var notice = StartupDiagnostics.UnconfiguredMessage(app);
                app.Use(async (HttpContext context, RequestDelegate _) =>
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.ContentType = "text/plain; charset=utf-8";
                    await context.Response.WriteAsync(notice);
                });
            }
        }

        app.UseRouting();

        var serveCert = managedHttps?.State == ManagedHttpsState.Managed;
        if (serveCert)
        {
            app.UseRateLimiter();
        }

        app.UseAuthentication();

        // Mounted ahead of UseAuthorization on purpose: the global fallback policy is
        // cookie-only, and applying it here would shut out bearer callers. The branch runs
        // the RouteAccess policy itself.
        app.MapDevConduitRoute();

        app.UseAuthorization();

        // Placed after authorization so the fallback policy also guards the console assets.
        app.UseStaticFiles();

        if (serveCert && OperatingSystem.IsWindows())
        {
            // Internal: certificate sync for clients that imported a .conduit, behind mTLS.
            app.MapCertEndpoints();
        }

        app.MapControllers();
        app.MapDevConduitApi();
        app.MapFallbackToFile("index.html");

        return true;
    }

    /// <summary>
    /// The application's own folder when running as a service, whose working directory is
    /// <c>C:\Windows\System32</c>, or when running from a published folder, recognisable by
    /// its <c>wwwroot</c>, so the exe finds its settings and console however it is launched.
    /// A build output folder has no <c>wwwroot</c>, so <c>dotnet run</c> keeps the default,
    /// the project directory, where the console's files live during development.
    /// </summary>
    public static string? ResolveContentRoot(bool runningAsService)
    {
        var baseDirectory = AppContext.BaseDirectory;

        return runningAsService || Directory.Exists(Path.Combine(baseDirectory, "wwwroot"))
            ? baseDirectory
            : null;
    }
}
