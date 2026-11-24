using System.Text;
using Microsoft.Extensions.Configuration;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Configuration;

/// <summary>
/// <c>"Enabled": false</c> on a remote or route, which is how a settings file layered above
/// appsettings.json switches off an entry it cannot delete.
/// </summary>
public sealed class RoutingConfigurationParserEnabledTests
{
    private const string UatEndpoint = "https://devconduit-uat.corp:8091";
    private const string ProdEndpoint = "https://devconduit-prod.corp:8091";

    private static IConfigurationSection Entry(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>($"entry:{value.Key}", value.Value)))
            .Build()
            .GetSection("entry");

    /// <summary>Parses two JSON documents layered as appsettings.json and a settings file above it.</summary>
    private static RoutingConfiguration ParseLayered(string lower, string upper)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(lower)))
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(upper)))
            .Build();

        return RoutingConfigurationParser.Parse(configuration.GetSection("DevConduit"), configuration.GetSection("AzureAd"));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    [InlineData(" false ")]
    public void IsEnabled_IsFalseOnlyForFalse(string value) =>
        Assert.False(RoutingConfigurationParser.IsEnabled(Entry(("Enabled", value))));

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no")]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("flase")]
    [InlineData(null)]
    public void IsEnabled_KeepsAnEntryWhoseEnabledIsTrueMissingOrUnreadable(string? value) =>
        Assert.True(RoutingConfigurationParser.IsEnabled(Entry(("Enabled", value), ("Endpoint", "x"))));

    [Fact]
    public void IsEnabled_KeepsAnEntryWithNoEnabledAtAll() =>
        Assert.True(RoutingConfigurationParser.IsEnabled(Entry(("Endpoint", "x"))));

    [Fact]
    public void Parse_LeavesOutADisabledRemote()
    {
        var routing = TestOptions.ParseRouting(
            ("Remotes:uat", UatEndpoint),
            ("Remotes:prod:Endpoint", ProdEndpoint),
            ("Remotes:prod:Enabled", "false"));

        Assert.Equal("uat", Assert.Single(routing.Remotes).Name);
        Assert.Empty(routing.Problems);
    }

    [Fact]
    public void Parse_LeavesOutADisabledRoute()
    {
        var routing = TestOptions.ParseRouting(
            ("Remotes:uat", UatEndpoint),
            ("Routes:a", "http://10.0.0.1:8080"),
            ("Routes:b:Target", "http://10.0.0.2:8080"),
            ("Routes:b:Enabled", "false"));

        Assert.Equal("a", Assert.Single(routing.Routes).Name);
        Assert.Empty(routing.Problems);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("")]
    public void Parse_KeepsARemoteAndRouteWhoseEnabledIsNotFalse(string value)
    {
        var routing = TestOptions.ParseRouting(
            ("Remotes:prod:Endpoint", ProdEndpoint),
            ("Remotes:prod:Enabled", value),
            ("Routes:a:Target", "http://10.0.0.1:8080"),
            ("Routes:a:Enabled", value));

        Assert.Equal("prod", Assert.Single(routing.Remotes).Name);
        Assert.Equal("a", Assert.Single(routing.Routes).Name);
    }

    [Fact]
    public void Parse_LetsAnUpperFileSwitchOffAStringRemoteFromALowerFile()
    {
        var routing = ParseLayered(
            $$"""{ "DevConduit": { "Remotes": { "uat": "{{UatEndpoint}}", "prod": "{{ProdEndpoint}}" } } }""",
            """{ "DevConduit": { "Remotes": { "prod": { "Enabled": false } } } }""");

        Assert.Equal("uat", Assert.Single(routing.Remotes).Name);
        Assert.Empty(routing.Problems);
    }

    [Fact]
    public void Parse_LetsAnUpperFileSwitchOffAStringRouteFromALowerFile()
    {
        var routing = ParseLayered(
            $$"""{ "DevConduit": { "Remotes": { "uat": "{{UatEndpoint}}" }, "Routes": { "a": "http://10.0.0.1:8080", "b": "http://10.0.0.2:8080" } } }""",
            """{ "DevConduit": { "Routes": { "a": { "Enabled": false } } } }""");

        Assert.Equal("b", Assert.Single(routing.Routes).Name);
    }

    [Fact]
    public void Parse_LetsAnUpperFileSwitchBackOnWhatALowerFileSwitchedOff()
    {
        var routing = ParseLayered(
            $$"""{ "DevConduit": { "Remotes": { "uat": { "Endpoint": "{{UatEndpoint}}", "Enabled": false } } } }""",
            """{ "DevConduit": { "Remotes": { "uat": { "Enabled": true } } } }""");

        Assert.Equal("uat", Assert.Single(routing.Remotes).Name);
    }

    [Fact]
    public void Parse_ReportsARouteOnADisabledRemoteAsItReportsAnUnknownRemote()
    {
        var routing = ParseLayered(
            $$"""
            {
              "DevConduit": {
                "Remotes": { "uat": "{{UatEndpoint}}", "prod": "{{ProdEndpoint}}" },
                "Routes": { "orders": { "Target": "http://10.50.1.10:8080", "Remote": "prod" } }
              }
            }
            """,
            """{ "DevConduit": { "Remotes": { "prod": { "Enabled": false } } } }""");

        var unknown = TestOptions.ParseRouting(
            ("Remotes:uat", UatEndpoint),
            ("Routes:orders:Target", "http://10.50.1.10:8080"),
            ("Routes:orders:Remote", "prod"));

        var problem = Assert.Single(routing.Problems);
        Assert.Equal(Assert.Single(unknown.Problems), problem);
        Assert.StartsWith("DevConduit:Routes:orders names the remote 'prod', which is not under DevConduit:Remotes.", problem);
        Assert.Contains("Configured remotes: uat.", problem);
        Assert.Null(routing.RemoteFor(routing.Routes[0]));
    }

    [Fact]
    public void Parse_ReportsMissingRemotesWhenTheOnlyRemoteIsDisabledEvenWithRoutesPointingAtIt()
    {
        // With no remote left, the routes are not checked one by one: the missing remotes are the one problem.
        var routing = TestOptions.ParseRouting(
            ("Remotes:prod:Endpoint", ProdEndpoint),
            ("Remotes:prod:Enabled", "false"),
            ("Routes:orders:Target", "http://10.50.1.10:8080"),
            ("Routes:orders:Remote", "prod"));

        Assert.Empty(routing.Remotes);
        var problem = Assert.Single(routing.Problems);
        Assert.StartsWith("DevConduit:Remotes is required in local mode.", problem);
    }

    [Fact]
    public void Parse_ReportsADefaultRemoteThatIsDisabledAsItReportsAnUnknownOne()
    {
        var routing = TestOptions.ParseRouting(
            ("Remotes:uat", UatEndpoint),
            ("Remotes:prod:Endpoint", ProdEndpoint),
            ("Remotes:prod:Enabled", "false"),
            ("DefaultRemote", "prod"));

        Assert.Null(routing.DefaultRemote);
        Assert.StartsWith("DevConduit:DefaultRemote ('prod') names no remote under DevConduit:Remotes.", Assert.Single(routing.Problems));
    }

    [Fact]
    public void Parse_MakesTheOnlyRemainingRemoteTheDefault()
    {
        var routing = TestOptions.ParseRouting(
            ("Remotes:uat", UatEndpoint),
            ("Remotes:prod:Endpoint", ProdEndpoint),
            ("Remotes:prod:Enabled", "false"),
            ("Routes:a", "http://10.0.0.1:8080"));

        Assert.Equal("uat", routing.DefaultRemote?.Name);
        Assert.Empty(routing.Problems);
    }

    [Fact]
    public void Parse_DoesNotReportProblemsWithADisabledRemote()
    {
        var routing = TestOptions.ParseRouting(
            ("Remotes:uat", UatEndpoint),
            ("Remotes:broken:Endpoint", "not a uri"),
            ("Remotes:broken:Enabled", "false"));

        Assert.Empty(routing.Problems);
    }
}
