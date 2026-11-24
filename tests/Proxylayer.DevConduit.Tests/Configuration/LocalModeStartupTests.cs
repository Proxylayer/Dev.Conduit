using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Configuration;

/// <summary>
/// The local-mode listener and identity wiring, resolved from a host built the way Program.cs
/// builds it. The AzureAd section names no tenant or client unless a test supplies one, so
/// <c>LocalTokenWarmup</c> has nothing to acquire and never starts a sign-in.
/// </summary>
public sealed class LocalModeStartupTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string ClientId = "22222222-2222-2222-2222-222222222222";
    private const string SharedHttpsEndpoint = "https://0.0.0.0:8091";

    private sealed class LocalHost : IAsyncDisposable
    {
        private readonly TempDirectory _contentRoot;

        private LocalHost(TempDirectory contentRoot, DevConduitOptions devConduit, WebApplication app)
        {
            _contentRoot = contentRoot;
            DevConduit = devConduit;
            App = app;
        }

        /// <summary>The options instance Program.cs would hold and pass to Register.</summary>
        public DevConduitOptions DevConduit { get; }

        public WebApplication App { get; }

        /// <summary>
        /// Builds a local-mode host on <paramref name="port"/> with the forward proxy off and
        /// <paramref name="overrides"/> applied on top of the defaults.
        /// </summary>
        public static LocalHost Build(int port, params (string Key, string? Value)[] overrides)
        {
            // An empty content root keeps the appsettings files copied next to the test
            // assembly out of the picture. The blank Entra values also mask any environment
            // variables that would otherwise give the warmup a tenant to sign in to.
            var contentRoot = new TempDirectory();
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["DevConduit:Mode"] = "Local",
                ["DevConduit:Local:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["DevConduit:Local:BindAddress"] = "127.0.0.1",
                ["DevConduit:Remotes:uat"] = "https://devconduit.corp:8091",
                ["DevConduit:Local:ForwardProxyEnabled"] = "false",
                ["DevConduit:Local:CacheDirectory"] = System.IO.Path.Combine(contentRoot.Path, "msal"),
                ["AzureAd:TenantId"] = "",
                ["AzureAd:ClientId"] = "",
            };

            foreach (var (key, value) in overrides)
            {
                values[key] = value;
            }

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = contentRoot.Path });
            builder.Configuration.AddInMemoryCollection(values);
            builder.Logging.ClearProviders();

            builder.Services.Configure<DevConduitOptions>(builder.Configuration.GetSection(DevConduitOptions.SectionName));
            var devConduit = builder.Configuration.GetSection(DevConduitOptions.SectionName).Get<DevConduitOptions>()
                           ?? new DevConduitOptions();

            var routing = RoutingConfigurationParser.Parse(
                builder.Configuration.GetSection(DevConduitOptions.SectionName),
                builder.Configuration.GetSection("AzureAd"));
            builder.Services.AddSingleton(routing);

            Assert.Empty(LocalModeStartup.Validate(devConduit, routing));
            LocalModeStartup.Register(builder, devConduit);

            return new LocalHost(contentRoot, devConduit, builder.Build());
        }

        /// <summary>The single remote every test host configures, as the services resolve it.</summary>
        public RemoteDefinition Remote =>
            Assert.Single(App.Services.GetRequiredService<RoutingConfiguration>().Remotes);

        public KestrelServerOptions Kestrel =>
            App.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            _contentRoot.Dispose();
        }
    }

    private static int FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task Register_DropsTheKestrelEndpointsTheSharedConfigurationCarries()
    {
        await using var host = LocalHost.Build(
            FreeLoopbackPort(),
            ("Kestrel:Endpoints:Https:Url", SharedHttpsEndpoint),
            ("Kestrel:Endpoints:Https:Certificate:Path", "/certs/devconduit.pfx"));

        var loader = host.Kestrel.ConfigurationLoader;

        Assert.NotNull(loader);
        Assert.Empty(loader.Configuration.GetSection("Endpoints").GetChildren());
        Assert.Empty(loader.Configuration.AsEnumerable());

        // The endpoint is still in the host configuration, which is what ModeBanner reports as ignored.
        Assert.Equal(SharedHttpsEndpoint, host.App.Configuration["Kestrel:Endpoints:Https:Url"]);
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("localhost", "localhost")]
    public async Task Start_ListensOnlyOnLoopbackOnTheLocalPort(string bindAddress, string expectedHost)
    {
        var port = FreeLoopbackPort();
        await using var host = LocalHost.Build(
            port,
            ("DevConduit:Local:BindAddress", bindAddress),
            ("Kestrel:Endpoints:Https:Url", SharedHttpsEndpoint),
            ("urls", "http://0.0.0.0:0"));

        Assert.False(host.Remote.Identity.IsComplete);

        var cancellation = TestContext.Current.CancellationToken;
        await host.App.StartAsync(cancellation);
        try
        {
            var addresses = host.App.Services.GetRequiredService<IServer>()
                .Features.GetRequiredFeature<IServerAddressesFeature>()
                .Addresses;

            Assert.Equal([$"http://{expectedHost}:{port}"], addresses);
        }
        finally
        {
            await host.App.StopAsync(cancellation);
        }
    }

    [Fact]
    public async Task Register_InheritsTheAzureAdIdentityIntoTheRemote()
    {
        await using var host = LocalHost.Build(
            FreeLoopbackPort(),
            ("AzureAd:TenantId", TenantId),
            ("AzureAd:ClientId", ClientId));

        var remote = host.Remote;
        Assert.Equal(TenantId, remote.Identity.TenantId);
        Assert.Equal(ClientId, remote.Identity.ClientId);
        Assert.True(remote.Identity.IsComplete);
        Assert.Equal([$"api://{ClientId}/DevConduit.Forward"], remote.Scopes);
    }

    [Fact]
    public async Task Register_KeepsAnExplicitLocalIdentity()
    {
        const string localClientId = "55555555-5555-5555-5555-555555555555";

        await using var host = LocalHost.Build(
            FreeLoopbackPort(),
            ("AzureAd:TenantId", TenantId),
            ("AzureAd:ClientId", ClientId),
            ("DevConduit:Remotes:uat", null),
            ("DevConduit:Remotes:uat:Endpoint", "https://devconduit.corp:8091"),
            ("DevConduit:Remotes:uat:ClientId", localClientId));

        var remote = host.Remote;
        Assert.Equal(TenantId, remote.Identity.TenantId);
        Assert.Equal(localClientId, remote.Identity.ClientId);
    }
}
