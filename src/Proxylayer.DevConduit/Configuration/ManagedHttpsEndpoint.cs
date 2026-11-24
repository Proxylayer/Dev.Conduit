using System.Runtime.Versioning;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Endpoints;

namespace Proxylayer.DevConduit.Configuration;

/// <summary>How the server's HTTPS endpoint was decided.</summary>
public enum ManagedHttpsState
{
    /// <summary><c>DevConduit:Server:Hostname</c> is not set; the listen defaults apply.</summary>
    NotConfigured,

    /// <summary>The host name or port is unusable; the listen defaults apply.</summary>
    Invalid,

    /// <summary><c>Kestrel:Endpoints</c> is configured explicitly and wins; the managed certificate is not used.</summary>
    Overridden,

    /// <summary>HTTPS on every interface with the app-managed certificate, and <c>/cert</c>.</summary>
    Managed
}

/// <summary>
/// Adds the server's HTTPS endpoint when <c>DevConduit:Server:Hostname</c> is set: <c>https://0.0.0.0:{Port}</c>,
/// served with the app-managed certificate through a certificate selector so a rotation needs
/// no restart. Client certificates are asked for only when <c>/cert</c> asks
/// (<see cref="ClientCertificateMode.DelayCertificate"/>), so the console never prompts for one,
/// and they are validated there rather than by Kestrel. An explicit <c>Kestrel:Endpoints</c>
/// still wins.
/// </summary>
public sealed record ManagedHttpsEndpoint(ManagedHttpsState State, string? Url, string? Message)
{
    /// <summary>The name of the endpoint under <c>Kestrel:Endpoints</c>.</summary>
    public const string EndpointName = "ManagedHttps";

    /// <summary>Decides and, when managed, registers the endpoint and the certificate services.</summary>
    public static ManagedHttpsEndpoint Configure(WebApplicationBuilder builder, DevConduitOptions devConduit)
    {
        var server = devConduit.Server;
        if (!server.IsManaged)
        {
            return new ManagedHttpsEndpoint(ManagedHttpsState.NotConfigured, null, null);
        }

        if (server.Problem() is { } problem)
        {
            return new ManagedHttpsEndpoint(ManagedHttpsState.Invalid, null, $"{problem} The app-managed HTTPS endpoint is off until it is corrected.");
        }

        var explicitEndpoints = ExplicitEndpointUrls(builder.Configuration);
        if (explicitEndpoints.Count > 0)
        {
            return new ManagedHttpsEndpoint(
                ManagedHttpsState.Overridden,
                null,
                $"Kestrel:Endpoints is configured ({string.Join(", ", explicitEndpoints)}), so it is used as it is and the app-managed "
                + $"certificate for {server.NormalizedHostname} is not: no HTTPS endpoint is added and /cert is not served. Remove "
                + "Kestrel:Endpoints from configuration to use the managed certificate.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return new ManagedHttpsEndpoint(ManagedHttpsState.Invalid, null, "The app-managed certificate needs Windows DPAPI; the HTTPS endpoint is off.");
        }

        Register(builder, devConduit);
        return new ManagedHttpsEndpoint(ManagedHttpsState.Managed, server.ListenUrl, null);
    }

    /// <summary>
    /// The URLs configured under <c>Kestrel:Endpoints</c>. Any one of them overrides the managed
    /// endpoint: the server then serves exactly those, and neither the app-managed certificate
    /// nor <c>/cert</c>. The server, the export command and the tray's console link all decide by this.
    /// </summary>
    public static IReadOnlyList<string> ExplicitEndpointUrls(IConfiguration configuration) =>
        configuration.GetSection("Kestrel:Endpoints")
            .GetChildren()
            .Select(endpoint => endpoint["Url"]?.Trim())
            .Where(url => !string.IsNullOrEmpty(url))
            .Select(url => url!)
            .ToList();

    /// <summary>True when <see cref="ExplicitEndpointUrls"/> names any URL.</summary>
    public static bool IsOverridden(IConfiguration configuration) => ExplicitEndpointUrls(configuration).Count > 0;

    public void Report(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Dev.Conduit.Startup");
        switch (State)
        {
            case ManagedHttpsState.Managed:
                logger.LogInformation("HTTPS on {Url} with the app-managed certificate; client certificates are asked for on /cert only.", Url);
                break;
            case ManagedHttpsState.Invalid:
                logger.LogError("{Message}", Message);
                break;
            case ManagedHttpsState.Overridden:
                logger.LogWarning("{Message}", Message);
                break;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Register(WebApplicationBuilder builder, DevConduitOptions devConduit)
    {
        builder.Configuration[$"Kestrel:Endpoints:{EndpointName}:Url"] = devConduit.Server.ListenUrl;

        var store = ServerCertificateStore.ForDataRoot(devConduit.ResolveDataRoot());
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(provider => new ServerCertificateAuthority(
            store,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("Dev.Conduit.Certificates")));
        builder.Services.AddSingleton(provider => ServerSelfTrust.Create(
            store,
            devConduit.Server.TrustStore,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("Dev.Conduit.Certificates")));
        builder.Services.AddSingleton<ServerCertificateManager>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<ServerCertificateManager>());
        builder.Services.AddCertEndpointRateLimiting();

        builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https =>
        {
            var manager = options.ApplicationServices.GetRequiredService<ServerCertificateManager>();
            https.ServerCertificateSelector = (_, _) => manager.SelectCertificate();
            https.ClientCertificateMode = ClientCertificateMode.DelayCertificate;

            // Kestrel would otherwise check a client certificate against Windows trust, which
            // the client authority is never part of. /cert validates it against that authority.
            https.ClientCertificateValidation = (_, _, _) => true;
            https.CheckCertificateRevocation = false;
        }));
    }
}
