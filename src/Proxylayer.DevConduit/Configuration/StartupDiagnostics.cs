using System.Net;
using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// Startup-time checks that tell the developer what still needs configuring
/// instead of failing later with an opaque sign-in error.
/// </summary>
public static class StartupDiagnostics
{
    public static void Report(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Dev.Conduit.Startup");
        var options = app.Services.GetRequiredService<IOptions<DevConduitOptions>>().Value;
        var identity = Identity(app.Configuration);

        if (IsUnconfigured(app))
        {
            logger.LogWarning(
                "\n============================================================\n{Message}============================================================",
                UnconfiguredMessage(app.Configuration));
        }
        else if (identity.Credential.Kind == EntraCredentialKind.None)
        {
            logger.LogWarning(
                "No Entra client credential is configured, so sign-in cannot redeem its authorization code. Configure {Options}",
                CredentialOptions);
        }
        else
        {
            logger.LogInformation(
                "Entra client credential: {Kind}, {Detail}.",
                identity.Credential.Kind,
                identity.Credential.Detail);
        }

        var unencrypted = UnencryptedNetworkListenUrls(app.Configuration);
        if (unencrypted.Count > 0)
        {
            logger.LogWarning(
                """

                ============================================================
                 Dev.Conduit listens on plain http beyond loopback: {Urls}
                 Sign-in tokens, session cookies and proxied traffic would
                 cross the network unencrypted. Use an https endpoint under
                 Kestrel:Endpoints, or keep http on localhost.
                ============================================================
                """,
                string.Join(", ", unencrypted));
        }

        ReportAccess(logger, options);
        ReportRoute(app, logger, options);
        ReportEgress(app, logger);
        ReportTargets(app, logger);

        logger.LogInformation("Request history and header sets are stored under {DataRoot}.", options.ResolveDataRoot());
    }

    private static void ReportAccess(ILogger logger, DevConduitOptions options)
    {
        if (ServerExposureWarnings.IsOpenToEveryUser(options.AllowedUserOids, options.AllowedGroupIds))
        {
            logger.LogWarning(
                "{Warning} DevConduit:AllowedUserOids and DevConduit:AllowedGroupIds are both empty.",
                ServerExposureWarnings.AnyUser);
        }
    }

    private static void ReportTargets(WebApplication app, ILogger logger)
    {
        var targets = app.Services.GetRequiredService<TargetAllowList>();

        foreach (var problem in targets.Problems)
        {
            logger.LogError("{Key} - {Problem}", TargetAllowList.SettingKey, problem);
        }

        if (!targets.IsRestricted)
        {
            logger.LogWarning(
                "{Warning} {Key} is empty, so /route and the console can call any host this server reaches, apart from DevConduit:BlockedEgressNetworks.",
                ServerExposureWarnings.AnyTarget,
                TargetAllowList.SettingKey);
        }
        else
        {
            logger.LogInformation(
                "Outbound calls are limited to the targets of the configured routes and {Key}: {Entries}.",
                TargetAllowList.SettingKey,
                targets.Entries.Count == 0 ? "(no usable entries)" : string.Join(", ", targets.Entries));
        }
    }

    private static void ReportRoute(WebApplication app, ILogger logger, DevConduitOptions options)
    {
        var aliases = app.Services.GetRequiredService<RouteAliasTable>();

        foreach (var problem in aliases.Problems)
        {
            logger.LogError("DevConduit:Routes - {Problem}", problem);
        }

        if (aliases.Names.Count > 0)
        {
            logger.LogInformation("Route aliases: {Aliases}.", string.Join(", ", aliases.Names));
        }
        else if (options.Route.AliasesOnly)
        {
            logger.LogWarning(
                "DevConduit:Route:AliasesOnly is true but no usable routes are configured under DevConduit:Routes, so /route will refuse every request.");
        }

        logger.LogInformation(
            "Routed traffic is captured in full, capped at {MaxEntries} entries and {MaxBodyBytes} bytes per body.",
            options.MaxRoutedHistoryEntries,
            options.MaxRoutedStoredBodyBytes);
    }

    private static void ReportEgress(WebApplication app, ILogger logger)
    {
        var guard = app.Services.GetRequiredService<EgressNetworkGuard>();

        foreach (var problem in guard.Problems)
        {
            logger.LogError("DevConduit:BlockedEgressNetworks - {Problem}", problem);
        }

        if (guard.Networks.Count > 0)
        {
            logger.LogInformation(
                "Outbound connections to these networks are refused: {Networks}.",
                string.Join(", ", guard.Networks));
        }
        else
        {
            logger.LogWarning(
                "DevConduit:BlockedEgressNetworks holds no usable networks, so outbound calls can reach the cloud metadata endpoint.");
        }
    }

    /// <summary>True while AzureAd:TenantId or AzureAd:ClientId still hold placeholder values.</summary>
    public static bool IsUnconfigured(WebApplication app)
    {
        var identity = Identity(app.Configuration);
        return IsPlaceholder(identity.TenantId) || IsPlaceholder(identity.ClientId);
    }

    /// <summary>The text shown in the log and served in place of the console until Entra ID is configured.</summary>
    public static string UnconfiguredMessage(WebApplication app) => UnconfiguredMessage(app.Configuration);

    /// <summary>
    /// The unconfigured notice for the given configuration. The redirect URIs it advises are
    /// derived from the URLs Kestrel listens on and <c>AzureAd:CallbackPath</c>, so the advice
    /// matches the deployed endpoint rather than assuming a local http one.
    /// </summary>
    public static string UnconfiguredMessage(IConfiguration configuration)
    {
        var identity = Identity(configuration);
        var callbackPath = CallbackPath(configuration);
        var redirectUris = ModeBanner.ResolveListenUrls(configuration)
            .Select(url => RedirectUri(url, callbackPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var heading = redirectUris.Count == 1
            ? "The app registration needs a Web platform redirect URI of"
            : "The app registration needs a Web platform redirect URI for each address users browse to:";
        var uriLines = string.Join(Environment.NewLine, redirectUris.Select(uri => "  " + uri));
        var hostNote = redirectUris.Any(uri => uri.Contains(WildcardHostPlaceholder, StringComparison.Ordinal))
            ? $"{Environment.NewLine}Replace {WildcardHostPlaceholder} with the name users browse to; the endpoint listens on every interface."
            : string.Empty;
        var pathNote = callbackPath == DefaultCallbackPath
            ? string.Empty
            : $" It registers {DefaultCallbackPath}, so change that redirect URI to end in {callbackPath} afterwards.";

        return $"""
            Dev.Conduit is NOT configured for Microsoft Entra ID.
            Sign-in cannot work until you set:
              AzureAd:TenantId  (currently: {identity.TenantId ?? "<not set>"})
              AzureAd:ClientId  (currently: {identity.ClientId ?? "<not set>"})
            in src/Proxylayer.DevConduit/appsettings.json.
            {CredentialLine(identity.Credential)}
            {heading}
            {uriLines}{hostNote}
            register-app.ps1 -ServerBaseUrl <url> sets this up, where <url> is that address without {callbackPath}.{pathNote}
            See the README for the full app registration walkthrough.

            """;
    }

    private const string DefaultCallbackPath = "/signin-oidc";

    /// <summary>Stands in for a listen address that binds every interface and so names no host.</summary>
    private const string WildcardHostPlaceholder = "<this server's host name>";

    private static readonly HashSet<string> WildcardHosts = ["0.0.0.0", "[::]", "*", "+"];

    private static string CallbackPath(IConfiguration configuration)
    {
        var path = configuration["AzureAd:CallbackPath"]?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            return DefaultCallbackPath;
        }

        return path.StartsWith('/') ? path : "/" + path;
    }

    /// <summary>
    /// <c>{scheme}://{host}:{port}{callbackPath}</c> for one Kestrel listen URL, with a
    /// wildcard host replaced by a placeholder.
    /// </summary>
    private static string RedirectUri(string listenUrl, string callbackPath)
    {
        var (scheme, host, port) = SplitListenUrl(listenUrl);

        if (WildcardHosts.Contains(host))
        {
            host = WildcardHostPlaceholder;
        }

        return $"{scheme}://{host}{port}{callbackPath}";
    }

    /// <summary>
    /// The server listen URLs that serve plain http on anything other than a loopback host.
    /// Wildcard hosts bind every interface, and Kestrel binds a host name other than
    /// <c>localhost</c> on every interface too, so only <c>localhost</c> and loopback
    /// addresses count as local.
    /// </summary>
    public static IReadOnlyList<string> UnencryptedNetworkListenUrls(IConfiguration configuration) =>
        ModeBanner.ResolveListenUrls(configuration)
            .Where(url =>
            {
                var (scheme, host, _) = SplitListenUrl(url);
                return scheme == "http" && !IsLoopbackHost(host);
            })
            .ToList();

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    /// <summary>
    /// Splits a Kestrel listen URL into its lower-cased scheme, its host, and the port suffix
    /// including the colon (empty when absent). Parsed by hand because Kestrel accepts
    /// wildcard hosts such as <c>*</c> and <c>+</c> that <see cref="Uri"/> rejects.
    /// </summary>
    private static (string Scheme, string Host, string Port) SplitListenUrl(string listenUrl)
    {
        var schemeEnd = listenUrl.IndexOf("://", StringComparison.Ordinal);
        var scheme = schemeEnd < 0 ? "http" : listenUrl[..schemeEnd].ToLowerInvariant();
        var authority = schemeEnd < 0 ? listenUrl : listenUrl[(schemeEnd + 3)..];

        var pathStart = authority.IndexOf('/');
        if (pathStart >= 0)
        {
            authority = authority[..pathStart];
        }

        var hostEnd = authority.StartsWith('[') ? authority.IndexOf(']') + 1 : authority.LastIndexOf(':');
        if (hostEnd <= 0)
        {
            hostEnd = authority.Length;
        }

        return (scheme, authority[..hostEnd], authority[hostEnd..]);
    }

    /// <summary>The two ways to give the app a client credential, preferred first.</summary>
    private const string CredentialOptions =
        """
        one of:
          AzureAd:ClientCredentials with a managed identity (preferred; see the README), or
          AzureAd:ClientSecret, with dotnet user-secrets in Development
            or the AzureAd__ClientSecret environment variable in production.
        """;

    private static string CredentialLine(EntraCredential credential) =>
        credential.Kind == EntraCredentialKind.None
            ? $"It also needs a client credential, {CredentialOptions}"
            : $"Client credential: {credential.Detail}.";

    /// <summary>
    /// The Entra settings are read straight from configuration rather than from
    /// MicrosoftIdentityOptions: Microsoft.Identity.Web binds those per authentication
    /// scheme, so the unnamed instance is always empty and every check would misfire.
    /// </summary>
    private static EntraSettings Identity(IConfiguration configuration)
    {
        var section = configuration.GetSection("AzureAd");
        return new EntraSettings(section["TenantId"], section["ClientId"], EntraCredential.Describe(section));
    }

    private sealed record EntraSettings(string? TenantId, string? ClientId, EntraCredential Credential);

    /// <summary>True for a blank Entra id or one that is still a placeholder.</summary>
    public static bool IsPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.StartsWith('<') || value.Contains("YOUR-", StringComparison.OrdinalIgnoreCase);
}
