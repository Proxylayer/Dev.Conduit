using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// Registrations that apply only when this process runs as the server: Entra sign-in for
/// the console, bearer validation for programmatic callers of <c>/route</c>, and the
/// allow-list authorization policies both are held to.
/// </summary>
public static class ServerModeStartup
{
    /// <summary>Where the server listens when configuration names no Kestrel endpoint and no urls.</summary>
    public const string DefaultListenUrl = "http://localhost:8091";

    /// <summary>The Entra authority host used when <c>AzureAd:Instance</c> is not configured.</summary>
    public const string DefaultInstance = "https://login.microsoftonline.com/";

    public static ManagedHttpsEndpoint Register(WebApplicationBuilder builder, DevConduitOptions devConduit)
    {
        // DevConduit:Server:Hostname adds the HTTPS endpoint with the app-managed certificate,
        // unless Kestrel:Endpoints is configured explicitly.
        var managedHttps = ManagedHttpsEndpoint.Configure(builder, devConduit);

        // Otherwise Kestrel configuration, or urls / ASPNETCORE_URLS, is the only source of the
        // listen address. Without either the server stays on loopback; StartupDiagnostics warns
        // about any plain http listener that reaches beyond it.
        if (ModeBanner.ResolveConfiguredListenUrls(builder.Configuration).Count == 0)
        {
            builder.Configuration["Kestrel:Endpoints:Http:Url"] = DefaultListenUrl;
        }

        // Microsoft.Identity.Web has no default for the instance and refuses to build either
        // scheme's options without one (IDW10106), so the public cloud is supplied here.
        if (string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:Instance"]))
        {
            builder.Configuration["AzureAd:Instance"] = DefaultInstance;
        }

        // Interactive sign-in against Entra ID, cookie session afterwards. Token acquisition is
        // enabled so the authorization code is redeemed through MSAL with the credential in
        // AzureAd:ClientCredentials (the managed identity assertion, or the client secret
        // fallback). Without it the OpenID Connect handler redeems the code itself and only
        // knows AzureAd:ClientSecret, so Entra rejects the redemption with AADSTS7000218.
        // No downstream API scopes are requested; the cache only holds the sign-in result.
        builder.Services
            .AddMicrosoftIdentityWebAppAuthentication(builder.Configuration, "AzureAd")
            .EnableTokenAcquisitionToCallDownstreamApi()
            .AddInMemoryTokenCaches();

        // Bearer validation for programmatic callers of /route. Expected audience is
        // api://<client-id>, which the app registration must expose as an API.
        builder.Services.AddMicrosoftIdentityWebApiAuthentication(
            builder.Configuration,
            "AzureAd",
            jwtBearerScheme: JwtBearerDefaults.AuthenticationScheme);

        // Registering the API scheme second makes it the default; put the cookie back.
        // Everything except /route authenticates the browser session, and the console must
        // keep redirecting to Entra rather than answering 401.
        builder.Services.PostConfigure<AuthenticationOptions>(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            options.DefaultSignOutScheme = OpenIdConnectDefaults.AuthenticationScheme;
        });

        // Authorization code + query response mode keeps the correlation and nonce cookies
        // SameSite=Lax, which is what makes plain http on localhost work.
        builder.Services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.ResponseMode = OpenIdConnectResponseMode.Query;
            options.UsePkce = true;
            options.SaveTokens = false;
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.NonceCookie.SameSite = SameSiteMode.Lax;
            options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });

        builder.Services.PostConfigure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.Cookie.Name = RouteCookiePolicy.SessionCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.SlidingExpiration = true;
            options.ExpireTimeSpan = TimeSpan.FromHours(10);
        });

        var policy = DevConduitAuthorization.BuildPolicy(devConduit);
        builder.Services
            .AddAuthorizationBuilder()
            .SetDefaultPolicy(policy)
            .SetFallbackPolicy(policy)
            .AddPolicy(
                DevConduitAuthorization.RouteAccessPolicy,
                DevConduitAuthorization.BuildRoutePolicy(
                    devConduit,
                    JwtBearerDefaults.AuthenticationScheme,
                    CookieAuthenticationDefaults.AuthenticationScheme));

        builder.Services.AddControllersWithViews().AddMicrosoftIdentityUI();

        builder.Services.AddSingleton<BearerScopeGuard>();
        builder.Services.AddSingleton<IRouteEgress, UpstreamRouteEgress>();
        builder.Services.AddScoped<IRouteAuthorizer, RouteAuthorizer>();

        return managedHttps;
    }
}
