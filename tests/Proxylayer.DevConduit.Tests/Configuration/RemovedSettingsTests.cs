using Microsoft.Extensions.Configuration;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Tests.Configuration;

/// <summary>Which removed settings a configuration still carries, so startup can warn about each one.</summary>
public sealed class RemovedSettingsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value =>
                new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();

    [Fact]
    public void Find_IsEmptyWhenNeitherRemovedKeyIsPresent()
    {
        var found = RemovedSettings.Find(Config(
            ("DevConduit:Local:Port", "8092"),
            ("Kestrel:Endpoints:Http:Url", "http://localhost:8091")));

        Assert.Empty(found);
    }

    [Fact]
    public void Find_IsEmptyForAnEmptyConfiguration() =>
        Assert.Empty(RemovedSettings.Find(Config()));

    [Fact]
    public void Find_ReportsThePort() =>
        Assert.Equal(["DevConduit:Port"], RemovedSettings.Find(Config(("DevConduit:Port", "8091"))));

    [Fact]
    public void Find_ReportsBindAllInterfaces() =>
        Assert.Equal(
            ["DevConduit:BindAllInterfaces"],
            RemovedSettings.Find(Config(("DevConduit:BindAllInterfaces", "true"))));

    [Fact]
    public void Find_ReportsBothInAFixedOrder()
    {
        var found = RemovedSettings.Find(Config(
            ("DevConduit:BindAllInterfaces", "false"),
            ("DevConduit:Port", "8091")));

        Assert.Equal(["DevConduit:Port", "DevConduit:BindAllInterfaces"], found);
    }

    [Fact]
    public void Find_ReportsAKeyPresentWithAnEmptyValue()
    {
        var found = RemovedSettings.Find(Config(
            ("DevConduit:Port", ""),
            ("DevConduit:BindAllInterfaces", "")));

        Assert.Equal(["DevConduit:Port", "DevConduit:BindAllInterfaces"], found);
    }

    [Fact]
    public void Find_MatchesTheKeyRegardlessOfCase() =>
        Assert.Equal(["DevConduit:Port"], RemovedSettings.Find(Config(("devconduit:PORT", "8091"))));

    [Theory]
    [InlineData("DevConduit:Route:Aliases:orders", "DevConduit:Route:Aliases", "DevConduit:Routes")]
    [InlineData("DevConduit:Local:RemoteEndpoint", "DevConduit:Local:RemoteEndpoint", "DevConduit:Remotes")]
    [InlineData("DevConduit:Local:Targets:0", "DevConduit:Local:Targets", "DevConduit:Remotes:<name>:Targets")]
    [InlineData("DevConduit:Local:TenantId", "DevConduit:Local:TenantId", "DevConduit:Remotes:<name>")]
    [InlineData("DevConduit:Local:ClientId", "DevConduit:Local:ClientId", "DevConduit:Remotes:<name>")]
    [InlineData("DevConduit:Local:Scopes:0", "DevConduit:Local:Scopes", "DevConduit:Remotes:<name>")]
    [InlineData("DevConduit:Port", "DevConduit:Port", "Kestrel:Endpoints")]
    [InlineData("DevConduit:BindAllInterfaces", "DevConduit:BindAllInterfaces", "Kestrel:Endpoints")]
    public void Warnings_NameEachRetiredKeyAndItsReplacement(string configuredKey, string reported, string replacement)
    {
        var configuration = Config((configuredKey, "value"));

        Assert.Equal([reported], RemovedSettings.Find(configuration));

        var warning = Assert.Single(RemovedSettings.Warnings(configuration));
        Assert.StartsWith($"{reported} has been removed and is ignored. ", warning);
        Assert.Contains(replacement, warning);
        Assert.EndsWith("Remove the key from configuration.", warning);
    }

    [Theory]
    [InlineData("DevConduit:Local:TenantId", "AzureAd:TenantId")]
    [InlineData("DevConduit:Local:ClientId", "AzureAd:ClientId")]
    [InlineData("DevConduit:Local:Scopes", "api://<client-id>/DevConduit.Forward")]
    public void Warnings_SayWhatAnOmittedIdentitySettingDefaultsTo(string key, string fallback) =>
        Assert.Contains(fallback, Assert.Single(RemovedSettings.Warnings(Config((key, "value")))));

    [Fact]
    public void Find_ReportsEveryRetiredKeyInAFixedOrder()
    {
        var found = RemovedSettings.Find(Config(
            ("DevConduit:Local:Scopes:0", "api://x/DevConduit.Forward"),
            ("DevConduit:Local:ClientId", "client"),
            ("DevConduit:Local:TenantId", "tenant"),
            ("DevConduit:Local:Targets:0", "*.corp"),
            ("DevConduit:Local:RemoteEndpoint", "https://devconduit.corp:8091"),
            ("DevConduit:Route:Aliases:orders", "http://10.0.0.5:8080"),
            ("DevConduit:BindAllInterfaces", "true"),
            ("DevConduit:Port", "8091")));

        Assert.Equal(
        [
            "DevConduit:Port",
            "DevConduit:BindAllInterfaces",
            "DevConduit:Route:Aliases",
            "DevConduit:Local:RemoteEndpoint",
            "DevConduit:Local:Targets",
            "DevConduit:Local:TenantId",
            "DevConduit:Local:ClientId",
            "DevConduit:Local:Scopes"
        ],
            found);
    }

    [Fact]
    public void Find_IgnoresTheSettingsThatReplacedTheRetiredOnes()
    {
        var found = RemovedSettings.Find(Config(
            ("DevConduit:Remotes:uat", "https://devconduit.corp:8091"),
            ("DevConduit:Remotes:prod:Targets:0", "*.prod.internal"),
            ("DevConduit:DefaultRemote", "uat"),
            ("DevConduit:Routes:orders", "http://10.0.0.5:8080"),
            ("DevConduit:Route:AliasesOnly", "true"),
            ("DevConduit:Local:ForwardProxyPort", "8093"),
            ("AzureAd:TenantId", "tenant")));

        Assert.Empty(found);
    }
}
