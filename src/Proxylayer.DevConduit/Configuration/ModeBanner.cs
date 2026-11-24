using Proxylayer.DevConduit.Services;
using Proxylayer.DevConduit.Services.Local;

namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// Announces which mode the process came up in. One executable serves two roles, and the
/// difference between them — whether an Entra token is validated or acquired, whether the
/// next hop is the upstream or another Dev.Conduit — is not something to leave a developer
/// inferring from behaviour.
/// </summary>
public static class ModeBanner
{
    public static void Report(WebApplication app, DevConduitOptions options)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Dev.Conduit.Startup");

        if (options.Mode == DevConduitMode.Local)
        {
            ReportLocal(app, logger, options.Local);
            return;
        }

        logger.LogInformation(
            """

            ============================================================
             Dev.Conduit mode: SERVER
             Listening on            {ListenUrls}
             Inbound authentication  Microsoft Entra ID (bearer or console session)
             Forwards to             the upstream named by each request
             Routed traffic capture  on
            ============================================================
            """,
            string.Join(", ", ResolveListenUrls(app.Configuration)));
    }

    /// <summary>
    /// The URLs Kestrel will listen on, read from configuration the same way Kestrel reads it:
    /// every <c>Kestrel:Endpoints:*:Url</c> first, then the <c>urls</c> key that
    /// <c>ASPNETCORE_URLS</c> and <c>--urls</c> bind to, then Kestrel's own default. The banner
    /// is logged before the server starts, so the bound addresses are not available yet.
    /// </summary>
    public static IReadOnlyList<string> ResolveListenUrls(IConfiguration configuration)
    {
        var configured = ResolveConfiguredListenUrls(configuration);
        return configured.Count > 0 ? configured : ["http://localhost:5000"];
    }

    /// <summary>
    /// The listen URLs configuration names explicitly: every <c>Kestrel:Endpoints:*:Url</c>,
    /// or failing that the entries of the <c>urls</c> key. Empty when configuration names none
    /// and Kestrel would fall back to its own default.
    /// </summary>
    public static IReadOnlyList<string> ResolveConfiguredListenUrls(IConfiguration configuration)
    {
        var endpointUrls = configuration.GetSection("Kestrel:Endpoints")
            .GetChildren()
            .Select(endpoint => endpoint["Url"])
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!.Trim())
            .ToList();

        if (endpointUrls.Count > 0)
        {
            return endpointUrls;
        }

        return (configuration["urls"] ?? string.Empty)
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static void ReportLocal(WebApplication app, ILogger logger, LocalModeOptions local)
    {
        var routing = app.Services.GetRequiredService<RoutingConfiguration>();

        logger.LogInformation(
            """

            ============================================================
             Dev.Conduit mode: LOCAL
             Listening on            {BindUrl}   (loopback only)
             Forward proxy           {ForwardProxy}
             Remote Dev.Conduit      {Remote}
             Inbound authentication  none{Secret}
             Outbound authentication Microsoft Entra ID, acquired for you
             Routed traffic capture  {Capture}
            ============================================================
            """,
            local.ResolveBindUrl(),
            local.ForwardProxyEnabled
                ? $"http://{local.BindAddress}:{local.ForwardProxyPort}"
                : "disabled",
            DescribeRemotes(routing),
            local.SharedSecret.Length > 0 ? $" (X-DevConduit-Local-Secret required on proxy ingress)" : string.Empty,
            local.CaptureEnabled ? "on" : "off (the remote instance holds the record)");

        var ignoredEndpoints = app.Configuration.GetSection("Kestrel:Endpoints")
            .GetChildren()
            .Where(endpoint => endpoint.Value is not null || endpoint.GetChildren().Any())
            .Select(endpoint => endpoint.Key)
            .ToList();
        if (ignoredEndpoints.Count > 0)
        {
            logger.LogInformation(
                "Local mode listens only on {BindUrl}; the Kestrel:Endpoints entries in configuration ({Endpoints}) are ignored.",
                local.ResolveBindUrl(),
                string.Join(", ", ignoredEndpoints));
        }

        foreach (var remote in routing.Remotes)
        {
            logger.LogInformation(
                "Remote '{Remote}': {Endpoint}, identity {Identity}, scopes {Scopes}.",
                remote.Name,
                remote.BaseAddress?.AbsoluteUri ?? remote.Endpoint,
                remote.DescribeIdentity(),
                string.Join(", ", remote.Scopes));
        }

        logger.LogInformation(
            "Default remote: {DefaultRemote}.",
            routing.DefaultRemote?.Name
            ?? "none (every route names its remote, and header-targeted traffic must match a route or a Targets rule)");

        var aliases = app.Services.GetRequiredService<RouteAliasTable>();
        foreach (var problem in aliases.Problems)
        {
            logger.LogError("DevConduit:Routes - {Problem}", problem);
        }

        foreach (var route in routing.Routes)
        {
            if (aliases.TryResolve(route.Name, out var origin))
            {
                logger.LogInformation(
                    "Route '{Route}' -> {Target} via {Remote}.",
                    route.Name,
                    origin.GetLeftPart(UriPartial.Authority),
                    routing.RemoteFor(route)?.Name ?? "<no remote>");
            }
        }

        var matcher = app.Services.GetRequiredService<DevConduitTargetMatcher>();
        foreach (var remote in routing.Remotes)
        {
            var rules = matcher.RemoteRules
                .Where(rule => string.Equals(rule.Remote.Name, remote.Name, StringComparison.OrdinalIgnoreCase))
                .Select(rule => rule.Route is null ? rule.Rule.ToString() : $"{rule.Rule} (route {rule.Route})")
                .ToList();

            logger.LogInformation(
                "Forward-proxy target rules for '{Remote}': {Rules}.",
                remote.Name,
                rules.Count == 0 ? "none" : string.Join(", ", rules));
        }

        logger.LogInformation("The token cache lives in {CacheDirectory}.", local.ResolveCacheDirectory());
    }

    /// <summary>The banner's one-line summary of the remotes.</summary>
    private static string DescribeRemotes(RoutingConfiguration routing) =>
        routing.Remotes.Count switch
        {
            0 => "<not set>",
            1 => $"{routing.Remotes[0].Name} {routing.Remotes[0].BaseAddress?.AbsoluteUri ?? routing.Remotes[0].Endpoint}",
            _ => $"{routing.Remotes.Count} configured, default {routing.DefaultRemote?.Name ?? "none"}"
        };

    /// <summary>The block printed to the console when local mode refuses to start.</summary>
    public static string FatalMessage(IReadOnlyList<string> problems) =>
        $"""

         ============================================================
          Dev.Conduit cannot start in LOCAL mode.
         ============================================================
         {string.Join(Environment.NewLine + Environment.NewLine, problems.Select(problem => " - " + problem))}

         Fix the settings under DevConduit:Remotes, DevConduit:Routes and
         DevConduit:Local in appsettings.json, or start without --mode local
         to run as the server.

         """;
}
