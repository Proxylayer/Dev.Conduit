using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Configuration;

/// <summary>
/// The server-mode sign-in wiring, resolved from a built host configured with a managed
/// identity credential and no client secret. Only options are resolved, so Entra is never contacted.
/// </summary>
public sealed class ServerModeStartupTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string ClientId = "22222222-2222-2222-2222-222222222222";
    private const string ManagedIdentityClientId = "33333333-3333-3333-3333-333333333333";

    private sealed class ServerHost : IAsyncDisposable
    {
        private readonly TempDirectory _contentRoot;

        private ServerHost(TempDirectory contentRoot, IServiceCollection services, WebApplication app)
        {
            _contentRoot = contentRoot;
            Services = services;
            App = app;
        }

        public IServiceCollection Services { get; }

        public WebApplication App { get; }

        /// <summary>
        /// Builds the host from the managed identity configuration with <paramref name="overrides"/>
        /// applied on top; an override with a null value removes the key.
        /// </summary>
        public static ServerHost Build(params (string Key, string? Value)[] overrides)
        {
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
                ["AzureAd:TenantId"] = TenantId,
                ["AzureAd:ClientId"] = ClientId,
                ["AzureAd:CallbackPath"] = "/signin-oidc",
                ["AzureAd:ClientCredentials:0:SourceType"] = "SignedAssertionFromManagedIdentity",
                ["AzureAd:ClientCredentials:0:ManagedIdentityClientId"] = ManagedIdentityClientId,
            };

            foreach (var (key, value) in overrides)
            {
                if (value is null)
                {
                    values.Remove(key);
                }
                else
                {
                    values[key] = value;
                }
            }

            // An empty content root keeps the appsettings files copied next to the test
            // assembly out of the picture; the in-memory AzureAd section is the only one.
            var contentRoot = new TempDirectory();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = contentRoot.Path });
            builder.Configuration.AddInMemoryCollection(values);

            ServerModeStartup.Register(builder, new DevConduitOptions());

            return new ServerHost(contentRoot, builder.Services, builder.Build());
        }

        public OpenIdConnectOptions OpenIdConnect =>
            App.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
                .Get(OpenIdConnectDefaults.AuthenticationScheme);

        public JwtBearerOptions JwtBearer =>
            App.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                .Get(JwtBearerDefaults.AuthenticationScheme);

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            _contentRoot.Dispose();
        }
    }

    [Fact]
    public async Task Register_RedeemsTheAuthorizationCodeThroughMicrosoftIdentityWeb()
    {
        await using var host = ServerHost.Build();

        var handler = host.OpenIdConnect.Events.OnAuthorizationCodeReceived;

        // Regression guard for AADSTS7000218. Without token acquisition the OpenID Connect
        // handler redeems the code itself with only AzureAd:ClientSecret, which a managed
        // identity configuration does not have. The event is never null (it defaults to a
        // no-op), so the guard is that Microsoft.Identity.Web supplied the handler and the
        // code is redeemed through MSAL with AzureAd:ClientCredentials.
        Assert.NotNull(handler);
        Assert.NotEqual(typeof(OpenIdConnectEvents).Assembly, handler.Method.DeclaringType?.Assembly);
        Assert.Equal(
            typeof(MicrosoftIdentityWebAppAuthenticationBuilder).Assembly,
            handler.Method.DeclaringType?.Assembly);
    }

    [Fact]
    public async Task Register_KeepsTheCodeFlowWithPkceAndLaxCookies()
    {
        await using var host = ServerHost.Build();

        var options = host.OpenIdConnect;

        Assert.Equal(OpenIdConnectResponseType.Code, options.ResponseType);
        Assert.Equal(OpenIdConnectResponseMode.Query, options.ResponseMode);
        Assert.True(options.UsePkce);
        Assert.False(options.SaveTokens);
        Assert.Equal(SameSiteMode.Lax, options.CorrelationCookie.SameSite);
        Assert.Equal(SameSiteMode.Lax, options.NonceCookie.SameSite);
    }

    [Fact]
    public async Task Register_AddsTokenAcquisitionOnce()
    {
        await using var host = ServerHost.Build();

        Assert.Single(host.Services, descriptor => descriptor.ServiceType == typeof(ITokenAcquisition));

        using var scope = host.App.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<ITokenAcquisition>());
    }

    [Fact]
    public async Task Register_DefaultsToTheCookieSessionAndChallengesWithOpenIdConnect()
    {
        await using var host = ServerHost.Build();

        var options = host.App.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;

        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, options.DefaultScheme);
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, options.DefaultAuthenticateScheme);
        Assert.Equal(OpenIdConnectDefaults.AuthenticationScheme, options.DefaultChallengeScheme);
        Assert.Contains(options.Schemes, scheme => scheme.Name == JwtBearerDefaults.AuthenticationScheme);
    }

    [Fact]
    public async Task Register_BindsTheManagedIdentityCredentialWithoutAClientSecret()
    {
        await using var host = ServerHost.Build();

        var identity = host.App.Services.GetRequiredService<IOptionsMonitor<MicrosoftIdentityOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);

        Assert.Equal(ClientId, identity.ClientId);
        var credential = Assert.Single(identity.ClientCredentials ?? []);
        Assert.Equal(CredentialSource.SignedAssertionFromManagedIdentity, credential.SourceType);
        Assert.Equal(ManagedIdentityClientId, credential.ManagedIdentityClientId);
        Assert.True(string.IsNullOrEmpty(host.OpenIdConnect.ClientSecret));
    }

    [Fact]
    public async Task Register_DefaultsTheInstanceToThePublicCloudWhenItIsNotConfigured()
    {
        await using var host = ServerHost.Build(("AzureAd:Instance", null));

        var expected = $"https://login.microsoftonline.com/{TenantId}/v2.0";

        Assert.Equal(ServerModeStartup.DefaultInstance, host.App.Configuration["AzureAd:Instance"]);
        Assert.Equal(expected, host.OpenIdConnect.Authority);
        Assert.Equal(expected, host.JwtBearer.Authority);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Register_DefaultsABlankInstanceToThePublicCloud(string instance)
    {
        await using var host = ServerHost.Build(("AzureAd:Instance", instance));

        var expected = $"https://login.microsoftonline.com/{TenantId}/v2.0";

        Assert.Equal(expected, host.OpenIdConnect.Authority);
        Assert.Equal(expected, host.JwtBearer.Authority);
    }

    [Fact]
    public async Task Register_KeepsAnExplicitInstance()
    {
        await using var host = ServerHost.Build(("AzureAd:Instance", "https://login.microsoftonline.us/"));

        var expected = $"https://login.microsoftonline.us/{TenantId}/v2.0";

        Assert.Equal("https://login.microsoftonline.us/", host.App.Configuration["AzureAd:Instance"]);
        Assert.Equal(expected, host.OpenIdConnect.Authority);
        Assert.Equal(expected, host.JwtBearer.Authority);
    }

    [Fact]
    public async Task Register_ListensOnLoopbackWhenNoEndpointOrUrlsIsConfigured()
    {
        await using var host = ServerHost.Build();

        Assert.Equal(ServerModeStartup.DefaultListenUrl, host.App.Configuration["Kestrel:Endpoints:Http:Url"]);
        Assert.Equal("http://localhost:8091", ServerModeStartup.DefaultListenUrl);
        Assert.Equal([ServerModeStartup.DefaultListenUrl], ModeBanner.ResolveListenUrls(host.App.Configuration));
    }

    [Fact]
    public async Task Register_DoesNotAddTheDefaultWhenAnEndpointIsConfigured()
    {
        await using var host = ServerHost.Build(("Kestrel:Endpoints:Https:Url", "https://0.0.0.0:8091"));

        Assert.Null(host.App.Configuration["Kestrel:Endpoints:Http:Url"]);
        Assert.Equal(["https://0.0.0.0:8091"], ModeBanner.ResolveListenUrls(host.App.Configuration));
    }

    [Fact]
    public async Task Register_DoesNotAddTheDefaultWhenUrlsIsConfigured()
    {
        await using var host = ServerHost.Build(("urls", "https://devconduit.corp:9443"));

        Assert.Null(host.App.Configuration["Kestrel:Endpoints:Http:Url"]);
        Assert.Equal(["https://devconduit.corp:9443"], ModeBanner.ResolveListenUrls(host.App.Configuration));
    }
}
