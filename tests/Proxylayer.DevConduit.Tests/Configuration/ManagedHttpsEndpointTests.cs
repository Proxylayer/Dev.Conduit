using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Configuration;

/// <summary>
/// The server's own host settings, and the decision whether the app-managed HTTPS endpoint is
/// used. Builders are created but never built or run, so nothing binds a port.
/// </summary>
public sealed class ManagedHttpsEndpointTests
{
    private const string Host = "devconduit.example.internal";

    private static ServerHostOptions Server(string hostname = Host, int port = ServerHostOptions.DefaultPort, string clientAddress = "") =>
        new() { Hostname = hostname, Port = port, ClientAddress = clientAddress };

    // ---- ServerHostOptions ---------------------------------------------------------------

    [Fact]
    public void Defaults_AreUnmanagedOnPort8091()
    {
        var options = new ServerHostOptions();

        Assert.Equal(8091, ServerHostOptions.DefaultPort);
        Assert.Equal(8091, options.Port);
        Assert.False(options.IsManaged);
        Assert.Null(options.Problem());
        Assert.Equal("DevConduit:Server", ServerHostOptions.SectionName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsManaged_NeedsAHostname(string hostname) =>
        Assert.False(Server(hostname).IsManaged);

    [Fact]
    public void ListenUrl_IsEveryInterfaceOnThePort()
    {
        Assert.Equal("https://0.0.0.0:8091", Server().ListenUrl);
        Assert.Equal("https://0.0.0.0:8443", Server(port: 8443).ListenUrl);
    }

    [Fact]
    public void NormalizedHostname_IsLowerCaseWithoutATrailingDot() =>
        Assert.Equal(Host, Server(" DevConduit.Example.Internal. ").NormalizedHostname);

    [Theory]
    [InlineData("")]
    [InlineData("10.0.0.5")]
    [InlineData("gateway.corp")]
    [InlineData(" fd00::5 ")]
    public void Problem_IsNullForUsableSettings(string clientAddress) =>
        Assert.Null(Server(clientAddress: clientAddress).Problem());

    [Theory]
    [InlineData("10.0.0.5", 8091, "", "is not a DNS host name")]
    [InlineData("*.devconduit.internal", 8091, "", "is not a DNS host name")]
    [InlineData(Host, 0, "", "is not a TCP port")]
    [InlineData(Host, 65536, "", "is not a TCP port")]
    [InlineData(Host, 8091, "http://10.0.0.5", "is not an IP address or host name")]
    [InlineData(Host, 8091, "bad_name", "is not an IP address or host name")]
    public void Problem_NamesWhatIsWrong(string hostname, int port, string clientAddress, string expected) =>
        Assert.Contains(expected, Server(hostname, port, clientAddress).Problem());

    [Fact]
    public void Binding_ReadsTheSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DevConduit:Server:Hostname"] = Host,
                ["DevConduit:Server:Port"] = "8443",
                ["DevConduit:Server:ClientAddress"] = "10.0.0.5"
            })
            .Build();

        var options = configuration.GetSection(ServerHostOptions.SectionName).Get<ServerHostOptions>()!;

        Assert.Equal(Host, options.Hostname);
        Assert.Equal(8443, options.Port);
        Assert.Equal("10.0.0.5", options.ClientAddress);
    }

    // ---- ManagedHttpsEndpoint.Configure --------------------------------------------------

    private static (ManagedHttpsEndpoint Endpoint, WebApplicationBuilder Builder) Configure(
        ServerHostOptions server,
        params (string Key, string Value)[] configuration)
    {
        using var contentRoot = new TempDirectory();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = contentRoot.Path, Args = [] });
        foreach (var (key, value) in configuration)
        {
            builder.Configuration[key] = value;
        }

        var endpoint = ManagedHttpsEndpoint.Configure(builder, new DevConduitOptions { Server = server, DataRoot = contentRoot.Path });
        return (endpoint, builder);
    }

    [Fact]
    public void Configure_WithoutAHostname_IsNotConfigured()
    {
        var (endpoint, builder) = Configure(Server(""));

        Assert.Equal(ManagedHttpsState.NotConfigured, endpoint.State);
        Assert.Null(endpoint.Url);
        Assert.Null(endpoint.Message);
        Assert.Null(builder.Configuration[$"Kestrel:Endpoints:{ManagedHttpsEndpoint.EndpointName}:Url"]);
    }

    [Theory]
    [InlineData("10.0.0.5", 8091)]
    [InlineData(Host, 0)]
    public void Configure_WithUnusableSettings_IsInvalidAndAddsNothing(string hostname, int port)
    {
        var (endpoint, builder) = Configure(Server(hostname, port));

        Assert.Equal(ManagedHttpsState.Invalid, endpoint.State);
        Assert.Null(endpoint.Url);
        Assert.EndsWith("The app-managed HTTPS endpoint is off until it is corrected.", endpoint.Message);
        Assert.Null(builder.Configuration[$"Kestrel:Endpoints:{ManagedHttpsEndpoint.EndpointName}:Url"]);
    }

    [Fact]
    public void Configure_WithKestrelEndpoints_IsOverridden()
    {
        var (endpoint, builder) = Configure(Server(), ("Kestrel:Endpoints:Mine:Url", "https://0.0.0.0:443"));

        Assert.Equal(ManagedHttpsState.Overridden, endpoint.State);
        Assert.Null(endpoint.Url);
        Assert.Contains("https://0.0.0.0:443", endpoint.Message);
        Assert.Contains(Host, endpoint.Message);
        Assert.Null(builder.Configuration[$"Kestrel:Endpoints:{ManagedHttpsEndpoint.EndpointName}:Url"]);
    }

    [Fact]
    public void Configure_WithAKestrelEndpointThatHasNoUrl_IsStillManaged()
    {
        var (endpoint, _) = Configure(Server(), ("Kestrel:Endpoints:Mine:Protocols", "Http1"));

        Assert.Equal(ManagedHttpsState.Managed, endpoint.State);
    }

    [Fact]
    public void Configure_Managed_ListensOnEveryInterfaceOnTheDefaultPort()
    {
        var (endpoint, builder) = Configure(Server());

        Assert.Equal(ManagedHttpsState.Managed, endpoint.State);
        Assert.Equal("https://0.0.0.0:8091", endpoint.Url);
        Assert.Null(endpoint.Message);
        Assert.Equal("ManagedHttps", ManagedHttpsEndpoint.EndpointName);
        Assert.Equal("https://0.0.0.0:8091", builder.Configuration["Kestrel:Endpoints:ManagedHttps:Url"]);
    }

    [Fact]
    public void Configure_Managed_UsesTheConfiguredPort()
    {
        var (endpoint, builder) = Configure(Server(port: 9443));

        Assert.Equal("https://0.0.0.0:9443", endpoint.Url);
        Assert.Equal("https://0.0.0.0:9443", builder.Configuration["Kestrel:Endpoints:ManagedHttps:Url"]);
    }

    [Fact]
    public void Configure_Managed_RegistersTheCertificateServices()
    {
        var (_, builder) = Configure(Server());

        Assert.Contains(builder.Services, service => service.ServiceType == typeof(Proxylayer.DevConduit.Certificates.ServerCertificateManager));
        Assert.Contains(builder.Services, service => service.ServiceType == typeof(Proxylayer.DevConduit.Certificates.ServerCertificateAuthority));
    }
}
