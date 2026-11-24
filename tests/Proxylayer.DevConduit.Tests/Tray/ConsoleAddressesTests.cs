using Microsoft.Extensions.Configuration;
using Proxylayer.DevConduit.Tray;

namespace Proxylayer.DevConduit.Tests.Tray;

/// <summary>The server console address the tray opens, with and without the app-managed host name.</summary>
public sealed class ConsoleAddressesTests
{
    private const string Host = "devconduit.example.internal";

    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();

    [Fact]
    public void Server_WithAManagedHostname_IsHttpsOnTheDefaultPort() =>
        Assert.Equal(new Uri($"https://{Host}:8091/"), ConsoleAddresses.Server(Configuration(("DevConduit:Server:Hostname", Host))));

    [Fact]
    public void Server_WithAManagedHostnameAndPort_UsesBoth() =>
        Assert.Equal(
            new Uri($"https://{Host}:9443/"),
            ConsoleAddresses.Server(Configuration(("DevConduit:Server:Hostname", " DevConduit.Example.Internal. "), ("DevConduit:Server:Port", "9443"))));

    [Fact]
    public void Server_AnExplicitHttpsEndpointWinsOverTheManagedHostname() =>
        Assert.Equal(
            new Uri("https://proxy.corp:443/"),
            ConsoleAddresses.Server(Configuration(
                ("DevConduit:Server:Hostname", Host),
                ("Kestrel:Endpoints:Https:Url", "https://0.0.0.0:443"),
                ("Kestrel:Endpoints:Https:Certificate:Subject", "proxy.corp"))));

    [Theory]
    [InlineData("10.0.0.5", "8091")]
    [InlineData(Host, "0")]
    public void Server_WithUnusableManagedSettings_FallsBackToTheDefault(string hostname, string port) =>
        Assert.Equal(
            new Uri(ConsoleAddresses.DefaultServerUrl),
            ConsoleAddresses.Server(Configuration(("DevConduit:Server:Hostname", hostname), ("DevConduit:Server:Port", port))));

    [Fact]
    public void Server_WithoutAnything_IsTheDefault() =>
        Assert.Equal(new Uri("http://localhost:8091/"), ConsoleAddresses.Server(Configuration()));

    [Fact]
    public void Server_WithAnotherExplicitEndpoint_DoesNotLinkToTheManagedHostname() =>
        Assert.NotEqual(
            new Uri($"https://{Host}:8091/"),
            ConsoleAddresses.Server(Configuration(("DevConduit:Server:Hostname", Host), ("Kestrel:Endpoints:Http:Url", "http://0.0.0.0:8080"))));

    [Fact]
    public void Server_WithAnotherExplicitEndpoint_LinksToWhatIsServed()
    {
        var uri = ConsoleAddresses.Server(Configuration(("DevConduit:Server:Hostname", Host), ("Kestrel:Endpoints:Http:Url", "http://0.0.0.0:8080")));

        Assert.Equal(new Uri("http://localhost:8080/"), uri);
    }

    [Theory]
    [InlineData("Kestrel:Endpoints:Http:Url", "http://0.0.0.0:8080", true)]
    [InlineData("Kestrel:Endpoints:Anything:Url", "https://proxy.corp", true)]
    [InlineData("Kestrel:Endpoints:Http:Url", "  ", false)]
    [InlineData("Kestrel:Endpoints:Http:Protocols", "Http1", false)]
    public void TheTrayAndTheServer_DecideTheOverrideByTheSameRule(string key, string value, bool overridden)
    {
        var configuration = Configuration(("DevConduit:Server:Hostname", Host), (key, value));

        Assert.Equal(overridden, Proxylayer.DevConduit.Configuration.ManagedHttpsEndpoint.IsOverridden(configuration));
        Assert.Equal(!overridden, ConsoleAddresses.Server(configuration) == new Uri($"https://{Host}:8091/"));
    }
}
