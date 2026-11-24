using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Configuration;

/// <summary>
/// How <c>DevConduit:Remotes</c>, <c>DevConduit:Routes</c> and <c>DevConduit:DefaultRemote</c> are read,
/// which remote is the default, and which configurations are reported as problems.
/// </summary>
public sealed class RoutingConfigurationParserTests
{
    private const string UatEndpoint = "https://devconduit-uat.corp:8091";
    private const string ProdEndpoint = "https://devconduit-prod.corp:8091";

    [Fact]
    public void Parse_ReadsARemoteWrittenAsAString()
    {
        var routing = TestOptions.ParseRouting(("Remotes:uat", UatEndpoint));

        var remote = Assert.Single(routing.Remotes);
        Assert.Equal("uat", remote.Name);
        Assert.Equal(UatEndpoint, remote.Endpoint);
        Assert.Equal(new Uri(UatEndpoint + "/"), remote.BaseAddress);
        Assert.Empty(remote.Targets);
        Assert.Empty(routing.Problems);
    }

    [Fact]
    public void Parse_ReadsARemoteWrittenAsAnObject()
    {
        var routing = TestOptions.ParseRouting(
            ("Remotes:prod:Endpoint", ProdEndpoint),
            ("Remotes:prod:TenantId", "tenant-prod"),
            ("Remotes:prod:ClientId", "client-prod"),
            ("Remotes:prod:Scopes:0", "x"),
            ("Remotes:prod:Targets:0", "*.prod.internal"),
            ("Remotes:prod:Targets:1", "10.50.1.10:8080"));

        var remote = Assert.Single(routing.Remotes);
        Assert.Equal("prod", remote.Name);
        Assert.Equal(ProdEndpoint, remote.Endpoint);
        Assert.Equal(new Uri(ProdEndpoint + "/"), remote.BaseAddress);
        Assert.Equal(new RemoteIdentity("tenant-prod", "client-prod"), remote.Identity);
        Assert.Equal(["x"], remote.Scopes);
        Assert.Equal(["*.prod.internal", "10.50.1.10:8080"], remote.Targets);
        Assert.Equal("DevConduit:Remotes:prod", remote.ConfigurationPath);
        Assert.Empty(routing.Problems);
    }

    [Fact]
    public void Parse_ReadsASingleTargetWrittenAsAString()
    {
        var routing = TestOptions.ParseRouting(
            ("Remotes:prod:Endpoint", ProdEndpoint),
            ("Remotes:prod:Targets", " *.prod.internal "));

        Assert.Equal(["*.prod.internal"], Assert.Single(routing.Remotes).Targets);
    }

    [Fact]
    public void Parse_ReducesAnEndpointToItsOrigin()
    {
        var routing = TestOptions.ParseRouting(("Remotes:uat", "  https://devconduit-uat.corp:8091/some/path?q=1  "));

        var remote = Assert.Single(routing.Remotes);
        Assert.Equal("https://devconduit-uat.corp:8091/some/path?q=1", remote.Endpoint);
        Assert.Equal(new Uri("https://devconduit-uat.corp:8091/"), remote.BaseAddress);
    }

    [Fact]
    public void Parse_PrefersEndpointOverTheRemotesOwnValue()
    {
        // A remote cannot carry both a string value and children in JSON, but other providers allow it.
        var routing = TestOptions.ParseRouting(
            ("Remotes:uat", "https://ignored.corp:1"),
            ("Remotes:uat:Endpoint", UatEndpoint));

        Assert.Equal(UatEndpoint, Assert.Single(routing.Remotes).Endpoint);
    }

    [Fact]
    public void Parse_ReadsRoutesInBothForms()
    {
        var routing = TestOptions.ParseRouting(
            ("Remotes:uat", UatEndpoint),
            ("Remotes:prod", ProdEndpoint),
            ("DefaultRemote", "uat"),
            ("Routes:orders", "http://10.0.0.5:8080"),
            ("Routes:orders-prod:Target", "http://10.50.1.10:8080"),
            ("Routes:orders-prod:Remote", "prod"));

        Assert.True(routing.TryFindRoute("orders", out var plain));
        Assert.Equal(new RouteDefinition("orders", "http://10.0.0.5:8080", null), plain);

        Assert.True(routing.TryFindRoute("orders-prod", out var assigned));
        Assert.Equal(new RouteDefinition("orders-prod", "http://10.50.1.10:8080", "prod"), assigned);

        Assert.Equal("uat", routing.RemoteFor(plain)!.Name);
        Assert.Equal("prod", routing.RemoteFor(assigned)!.Name);
        Assert.Empty(routing.Problems);
    }

    [Fact]
    public void Parse_TreatsABlankRouteRemoteAsNone()
    {
        var routing = TestOptions.ParseRouting(
            ("Remotes:uat", UatEndpoint),
            ("Routes:orders:Target", "http://10.0.0.5:8080"),
            ("Routes:orders:Remote", "   "));

        Assert.Null(Assert.Single(routing.Routes).Remote);
        Assert.Equal("uat", routing.RemoteFor(routing.Routes[0])!.Name);
    }

    [Theory]
    [InlineData("PROD")]
    [InlineData("Prod")]
    [InlineData(" prod ")]
    public void TryFindRemote_IgnoresCase(string name)
    {
        var routing = TestOptions.ParseRouting(("Remotes:uat", UatEndpoint), ("Remotes:prod", ProdEndpoint));

        Assert.True(routing.TryFindRemote(name, out var remote));
        Assert.Equal("prod", remote.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("other")]
    public void TryFindRemote_FindsNothingForABlankOrUnknownName(string? name)
    {
        var routing = TestOptions.ParseRouting(("Remotes:uat", UatEndpoint));

        Assert.False(routing.TryFindRemote(name, out var remote));
        Assert.Null(remote);
    }

    [Fact]
    public void Parse_MatchesARouteRemoteAndDefaultRemoteRegardlessOfCase()
    {
        var routing = TestOptions.ParseRouting(
            ("Remotes:uat", UatEndpoint),
            ("Remotes:prod", ProdEndpoint),
            ("DefaultRemote", "UAT"),
            ("Routes:orders-prod:Target", "http://10.50.1.10:8080"),
            ("Routes:orders-prod:Remote", "PROD"));

        Assert.Empty(routing.Problems);
        Assert.Equal("uat", routing.DefaultRemote!.Name);
        Assert.Equal("prod", routing.RemoteFor(Assert.Single(routing.Routes))!.Name);
    }

    [Fact]
    public void TryFindRoute_IgnoresCase()
    {
        var routing = TestOptions.ParseRouting(("Remotes:uat", UatEndpoint), ("Routes:orders", "http://10.0.0.5:8080"));

        Assert.True(routing.TryFindRoute("ORDERS", out var route));
        Assert.Equal("orders", route.Name);
    }

    [Fact]
    public void Parse_MakesTheOnlyRemoteTheDefault()
    {
        var routing = TestOptions.ParseRouting(("Remotes:uat", UatEndpoint));

        Assert.Equal("uat", routing.DefaultRemote?.Name);
    }

    [Fact]
    public void Parse_PrefersAnExplicitDefaultRemote()
    {
        var routing = TestOptions.ParseRouting(
            ("Remotes:uat", UatEndpoint),
            ("Remotes:prod", ProdEndpoint),
            ("DefaultRemote", "prod"));

        Assert.Equal("prod", routing.DefaultRemote?.Name);
        Assert.Empty(routing.Problems);
    }

    [Fact]
    public void Parse_HasNoDefaultWithSeveralRemotesAndNoneChosen()
    {
        var routing = TestOptions.ParseRouting(("Remotes:uat", UatEndpoint), ("Remotes:prod", ProdEndpoint));

        Assert.Null(routing.DefaultRemote);

        // Several remotes and no default is fine as long as no route depends on a default.
        Assert.Empty(routing.Problems);
    }

    [Fact]
    public void Parse_OrdersRemotesByName()
    {
        var routing = TestOptions.ParseRouting(
            ("Remotes:uat", UatEndpoint),
            ("Remotes:alpha", "https://alpha.corp:8091"),
            ("Remotes:prod", ProdEndpoint));

        Assert.Equal(["alpha", "prod", "uat"], routing.Remotes.Select(remote => remote.Name));
    }

    [Fact]
    public void Parse_ReportsMissingRemotesFirst()
    {
        var routing = TestOptions.ParseRouting(("Routes:orders", "http://10.0.0.5:8080"), ("DefaultRemote", "uat"));

        Assert.Empty(routing.Remotes);
        Assert.Null(routing.DefaultRemote);

        // Neither the route nor the DefaultRemote repeats the missing-remotes problem.
        var problem = Assert.Single(routing.Problems);
        Assert.StartsWith("DevConduit:Remotes is required in local mode.", problem);
    }

    [Fact]
    public void Parse_ReportsMissingRemotesForAnEmptyConfiguration()
    {
        var routing = TestOptions.ParseRouting();

        Assert.Contains("DevConduit:Remotes is required", Assert.Single(routing.Problems));
    }

    [Theory]
    [InlineData("devconduit.corp:8091")]
    [InlineData("/relative/path")]
    [InlineData("ftp://devconduit.corp:8091")]
    [InlineData("ws://devconduit.corp:8091")]
    [InlineData("not a uri")]
    public void Parse_ReportsAnEndpointThatIsNotAnAbsoluteHttpUri(string endpoint)
    {
        var routing = TestOptions.ParseRouting(("Remotes:uat", endpoint));

        var remote = Assert.Single(routing.Remotes);
        Assert.Null(remote.BaseAddress);
        Assert.Equal(
            $"DevConduit:Remotes:uat endpoint ('{endpoint}') is not an absolute http or https URI.",
            Assert.Single(routing.Problems));
    }

    [Fact]
    public void Parse_ReportsARemoteWithNoEndpoint()
    {
        var routing = TestOptions.ParseRouting(("Remotes:uat:TenantId", "tenant"));

        Assert.Null(Assert.Single(routing.Remotes).BaseAddress);
        Assert.StartsWith("DevConduit:Remotes:uat has no endpoint.", Assert.Single(routing.Problems));
    }

    [Fact]
    public void Parse_AcceptsAnHttpEndpoint()
    {
        var routing = TestOptions.ParseRouting(("Remotes:uat", "http://devconduit-uat.corp:8091"));

        Assert.Equal(new Uri("http://devconduit-uat.corp:8091/"), Assert.Single(routing.Remotes).BaseAddress);
        Assert.Empty(routing.Problems);
    }

    [Fact]
    public void Parse_ReportsARouteNamingAnUnknownRemote()
    {
        var routing = TestOptions.ParseRouting(
            ("Remotes:uat", UatEndpoint),
            ("Routes:orders-prod:Target", "http://10.50.1.10:8080"),
            ("Routes:orders-prod:Remote", "prod"));

        var problem = Assert.Single(routing.Problems);
        Assert.StartsWith("DevConduit:Routes:orders-prod names the remote 'prod', which is not under DevConduit:Remotes.", problem);
        Assert.Contains("Configured remotes: uat.", problem);
        Assert.Null(routing.RemoteFor(routing.Routes[0]));
    }

    [Fact]
    public void Parse_ReportsAnUnknownDefaultRemoteOnce()
    {
        var routing = TestOptions.ParseRouting(
            ("Remotes:uat", UatEndpoint),
            ("Remotes:prod", ProdEndpoint),
            ("DefaultRemote", "staging"),
            ("Routes:a", "http://10.0.0.1:8080"),
            ("Routes:b", "http://10.0.0.2:8080"));

        Assert.Null(routing.DefaultRemote);
        var problem = Assert.Single(routing.Problems);
        Assert.StartsWith("DevConduit:DefaultRemote ('staging') names no remote under DevConduit:Remotes.", problem);
        Assert.Contains("prod, uat", problem);
    }

    [Fact]
    public void Parse_DoesNotFallBackToTheOnlyRemoteForAnUnknownDefaultRemote()
    {
        var routing = TestOptions.ParseRouting(("Remotes:uat", UatEndpoint), ("DefaultRemote", "prod"));

        Assert.Null(routing.DefaultRemote);
        Assert.Contains("DevConduit:DefaultRemote ('prod')", Assert.Single(routing.Problems));
    }

    [Fact]
    public void Parse_ReportsARouteWithNoRemoteAndNoDefault()
    {
        var routing = TestOptions.ParseRouting(
            ("Remotes:uat", UatEndpoint),
            ("Remotes:prod", ProdEndpoint),
            ("Routes:orders", "http://10.0.0.5:8080"));

        var problem = Assert.Single(routing.Problems);
        Assert.StartsWith("The route orders under DevConduit:Routes names no Remote", problem);
        Assert.Contains("Set DevConduit:DefaultRemote, or give each route a Remote.", problem);
        Assert.Null(routing.RemoteFor(routing.Routes[0]));
    }

    [Fact]
    public void Parse_ReportsEveryRouteWithNoRemoteAndNoDefaultInOneProblem()
    {
        var routing = TestOptions.ParseRouting(
            ("Remotes:uat", UatEndpoint),
            ("Remotes:prod", ProdEndpoint),
            ("Routes:a", "http://10.0.0.1:8080"),
            ("Routes:b", "http://10.0.0.2:8080"),
            ("Routes:c:Target", "http://10.0.0.3:8080"),
            ("Routes:c:Remote", "prod"));

        Assert.StartsWith("The routes a, b under DevConduit:Routes name no Remote", Assert.Single(routing.Problems));
    }

    [Fact]
    public void Parse_ReportsEveryProblemTogether()
    {
        var routing = TestOptions.ParseRouting(
            ("Remotes:uat", "not a uri"),
            ("Remotes:prod", ProdEndpoint),
            ("DefaultRemote", "staging"),
            ("Routes:orders:Target", "http://10.0.0.5:8080"),
            ("Routes:orders:Remote", "missing"));

        Assert.Equal(3, routing.Problems.Count);
        Assert.Contains(routing.Problems, problem => problem.StartsWith("DevConduit:Remotes:uat endpoint"));
        Assert.Contains(routing.Problems, problem => problem.StartsWith("DevConduit:DefaultRemote ('staging')"));
        Assert.Contains(routing.Problems, problem => problem.StartsWith("DevConduit:Routes:orders names the remote 'missing'"));
    }
}
