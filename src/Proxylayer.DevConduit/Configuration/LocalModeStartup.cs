using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Services;
using Proxylayer.DevConduit.Services.Local;

namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// Registrations that apply only when this process runs on a developer workstation: the
/// loopback bind, a fixed local principal in place of Entra sign-in, MSAL token
/// acquisition, and the two ingress styles that carry application traffic to the remote
/// instance.
/// </summary>
public static class LocalModeStartup
{
    /// <summary>
    /// Fatal configuration problems, checked before anything binds. A non-empty result must
    /// stop the process: every entry describes a state in which local mode would either be
    /// unsafe or unable to do its job. The routing problems come first: a missing or unusable
    /// remote is the most likely reason a workstation cannot start.
    /// </summary>
    public static IReadOnlyList<string> Validate(DevConduitOptions devConduit, RoutingConfiguration routing) =>
        [.. routing.Problems, .. devConduit.Local.FatalProblems()];

    public static void Register(WebApplicationBuilder builder, DevConduitOptions devConduit)
    {
        var local = devConduit.Local;

        builder.WebHost.ConfigureKestrel(options => ListenOnLoopbackOnly(options, local));

        builder.Services
            .AddAuthentication(LocalModeAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LocalModeAuthenticationHandler>(
                LocalModeAuthenticationHandler.SchemeName,
                configureOptions: null);

        // Every request already carries the local principal, so the allow-list policies
        // server mode enforces have nothing to decide here.
        builder.Services
            .AddAuthorizationBuilder()
            .SetDefaultPolicy(LocalPolicy())
            .SetFallbackPolicy(LocalPolicy());

        builder.Services.AddControllersWithViews();

        builder.Services.AddSingleton<LocalSharedSecret>();
        builder.Services.AddSingleton<DevConduitTargetMatcher>();
        builder.Services.AddSingleton<IDevConduitTokenProvider, MsalDevConduitTokenProvider>();
        builder.Services.AddSingleton<RemoteRelay>();
        builder.Services.AddSingleton<IRouteEgress, RemoteRelayRouteEgress>();
        builder.Services.AddScoped<IRouteAuthorizer, LocalRouteAuthorizer>();

        builder.Services.AddHostedService<LocalTokenWarmup>();

        // Keeps each imported server's pinned certificates and this machine's client
        // certificate current through the server's /cert endpoints.
        builder.Services.AddClientTrust();
        builder.Services.AddSingleton<ClientTrustStatusBoard>();
        builder.Services.AddSingleton<ClientCertificateSync>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<ClientCertificateSync>());

        if (local.ForwardProxyEnabled)
        {
            builder.Services.AddSingleton<ForwardProxyConnectionHandler>();
            builder.Services.AddHostedService<ForwardProxyServer>();
        }
    }

    /// <summary>
    /// Binds the local listener and nothing else. The appsettings shared with the server can
    /// carry Kestrel endpoints on every interface (the UAT https endpoint, for one), and local
    /// mode serves without inbound sign-in, so swapping in an empty configuration loader drops
    /// every <c>Kestrel:Endpoints</c> entry before Kestrel reads it. Kestrel also ignores urls
    /// and ASPNETCORE_URLS once an endpoint is defined in code. ModeBanner reports the
    /// endpoints that were ignored.
    /// </summary>
    private static void ListenOnLoopbackOnly(KestrelServerOptions options, LocalModeOptions local)
    {
        options.Configure(new ConfigurationBuilder().Build());

        if (string.Equals(local.BindAddress?.Trim(), "localhost", StringComparison.OrdinalIgnoreCase))
        {
            options.ListenLocalhost(local.Port);
            return;
        }

        // Validate has already refused to start on anything but a loopback address.
        if (!local.TryResolveBindAddress(out var address, out var error))
        {
            throw new InvalidOperationException(error);
        }

        options.Listen(address, local.Port);
    }

    private static AuthorizationPolicy LocalPolicy() =>
        new AuthorizationPolicyBuilder(LocalModeAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser()
            .Build();
}
