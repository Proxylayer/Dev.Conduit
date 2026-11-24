using Microsoft.Extensions.Logging.Abstractions;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Services;
using Proxylayer.DevConduit.Services.Local;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Services.Local;

/// <summary>Which destinations the forward proxy relays through the remote instance.</summary>
public sealed class DevConduitTargetMatcherTests
{
    private const string Remote = "https://devconduit.corp:8091";

    private static DevConduitTargetMatcher Matcher(params string[] targets) => MatcherWithAliases(targets);

    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("https://example.com/a/b")]
    [InlineData("http://example.com:9000/")]
    [InlineData("https://example.com:44300/x")]
    public void ShouldRoute_MatchesAnExactHostOnAnyPort(string url) =>
        Assert.True(Matcher("example.com").ShouldRoute(new Uri(url)));

    [Theory]
    [InlineData("http://example.com:8080/", true)]
    [InlineData("http://example.com:9090/", false)]
    [InlineData("http://example.com/", false)]
    public void ShouldRoute_HonoursAPortOnTheRule(string url, bool expected) =>
        Assert.Equal(expected, Matcher("example.com:8080").ShouldRoute(new Uri(url)));

    [Theory]
    [InlineData("EXAMPLE.COM")]
    [InlineData("Example.Com")]
    public void ShouldRoute_IgnoresCase(string rule) =>
        Assert.True(Matcher(rule).ShouldRoute(new Uri("http://example.com/")));

    [Theory]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://10.0.0.5:8080/api")]
    public void ShouldRoute_MatchesABareIPv4Address(string url) =>
        Assert.True(Matcher("10.0.0.5").ShouldRoute(new Uri(url)));

    [Fact]
    public void ShouldRoute_DoesNotMatchADifferentIPv4Address() =>
        Assert.False(Matcher("10.0.0.5").ShouldRoute(new Uri("http://10.0.0.6/")));

    [Theory]
    [InlineData("2001:db8::1")]
    [InlineData("[2001:db8::1]")]
    public void ShouldRoute_MatchesABareIPv6AddressWrittenEitherWay(string rule) =>
        Assert.True(Matcher(rule).ShouldRoute(new Uri("http://[2001:db8::1]/api")));

    [Fact]
    public void ShouldRoute_MatchesAnIPv6AddressWithAPort()
    {
        var matcher = Matcher("[2001:db8::1]:8080");

        Assert.True(matcher.ShouldRoute(new Uri("http://[2001:db8::1]:8080/")));
        Assert.False(matcher.ShouldRoute(new Uri("http://[2001:db8::1]:9090/")));
    }

    [Theory]
    [InlineData("http://a.example.com/")]
    [InlineData("http://a.b.example.com/")]
    [InlineData("https://deep.nested.sub.example.com/x")]
    public void ShouldRoute_MatchesSubdomainsOfALeadingWildcard(string url) =>
        Assert.True(Matcher("*.example.com").ShouldRoute(new Uri(url)));

    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("http://notexample.com/")]
    [InlineData("http://example.com.evil.test/")]
    [InlineData("http://xexample.com/")]
    public void ShouldRoute_DoesNotMatchTheApexOrALookalikeUnderAWildcard(string url) =>
        Assert.False(Matcher("*.example.com").ShouldRoute(new Uri(url)));

    [Fact]
    public void ShouldRoute_AppliesAPortToAWildcardRule()
    {
        var matcher = Matcher("*.example.com:8080");

        Assert.True(matcher.ShouldRoute(new Uri("http://a.example.com:8080/")));
        Assert.False(matcher.ShouldRoute(new Uri("http://a.example.com:9090/")));
    }

    [Fact]
    public void ShouldRoute_RefusesWhenNoRuleMatches() =>
        Assert.False(Matcher("example.com").ShouldRoute(new Uri("http://other.test/")));

    [Fact]
    public void ShouldRoute_RefusesWhenNoRulesAreConfigured() =>
        Assert.False(Matcher().ShouldRoute(new Uri("http://example.com/")));

    [Fact]
    public void ShouldRoute_NeverRelaysTrafficAlreadyAddressedToTheRemoteInstance()
    {
        var matcher = Matcher("devconduit.corp");

        Assert.False(matcher.ShouldRoute(new Uri("https://devconduit.corp:8091/route/api")));

        // A different port on the same host is not the remote listener, so the rule applies.
        Assert.True(matcher.ShouldRoute(new Uri("https://devconduit.corp:9091/route/api")));
    }

    [Theory]
    [InlineData("http://localhost:8092/")]
    [InlineData("http://LOCALHOST:8093/")]
    [InlineData("http://127.0.0.1:8092/")]
    [InlineData("http://127.0.0.1:8093/")]
    [InlineData("http://[::1]:8093/")]
    public void ShouldRoute_NeverRelaysTrafficAimedAtItsOwnListeners(string url) =>
        Assert.False(Matcher("localhost", "127.0.0.1", "[::1]").ShouldRoute(new Uri(url)));

    [Fact]
    public void ShouldRoute_StillRelaysLoopbackTrafficOnOtherPorts() =>
        Assert.True(Matcher("localhost").ShouldRoute(new Uri("http://localhost:9000/")));

    [Fact]
    public void Rules_DropWhatCannotBeParsed()
    {
        var matcher = Matcher("example.com", "   ", "example.com:notaport", "example.com:70000", "http://");

        Assert.Single(matcher.Rules);
        Assert.Equal("example.com", matcher.Rules[0].ToString());
    }

    private const string OrdersAlias = "http://10.0.0.5:8080";

    private static DevConduitTargetMatcher MatcherWithAliases(string[] targets, params (string Name, string Origin)[] aliases)
    {
        var routing = TestOptions.ParseRouting(
        [
            ("Remotes:remote:Endpoint", Remote),
            .. targets.Select((target, index) => ($"Remotes:remote:Targets:{index}", (string?)target)),
            .. aliases.Select(alias => ($"Routes:{alias.Name}", (string?)alias.Origin))
        ]);

        return new(routing, new RouteAliasTable(routing), TestOptions.Create(), NullLogger<DevConduitTargetMatcher>.Instance);
    }

    private static string[] RuleText(DevConduitTargetMatcher matcher) =>
        [.. matcher.Rules.Select(rule => rule.ToString())];

    [Theory]
    [InlineData("http://10.0.0.5:8080/", true)]
    [InlineData("http://10.0.0.5:8080/api/orders?id=1", true)]
    [InlineData("http://10.0.0.5:8081/", false)]
    [InlineData("http://10.0.0.5/", false)]
    [InlineData("http://10.0.0.6:8080/", false)]
    public void ShouldRoute_RelaysAnAliasOriginWithoutAnyTargets(string url, bool expected)
    {
        var matcher = MatcherWithAliases([], ("orders", OrdersAlias));

        Assert.Equal(expected, matcher.ShouldRoute(new Uri(url)));
        Assert.Equal(["10.0.0.5:8080"], RuleText(matcher));
    }

    [Fact]
    public void Rules_GiveAnHttpsAliasWithoutAPortPort443()
    {
        var matcher = MatcherWithAliases([], ("api", "https://api.example.com"));

        Assert.Equal(["api.example.com:443"], RuleText(matcher));
        Assert.True(matcher.ShouldRoute(new Uri("https://api.example.com/v1")));
        Assert.False(matcher.ShouldRoute(new Uri("http://api.example.com/v1")));
        Assert.False(matcher.ShouldRoute(new Uri("https://api.example.com:8443/v1")));
    }

    [Fact]
    public void Rules_GiveAnHttpAliasWithoutAPortPort80()
    {
        var matcher = MatcherWithAliases([], ("legacy", "http://legacy.example.com"));

        Assert.Equal(["legacy.example.com:80"], RuleText(matcher));
        Assert.True(matcher.ShouldRoute(new Uri("http://legacy.example.com/")));
        Assert.False(matcher.ShouldRoute(new Uri("https://legacy.example.com/")));
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("ftp://files.example.com")]
    [InlineData("http://example.com/with/a/path")]
    [InlineData("http://example.com/?q=1")]
    [InlineData("   ")]
    public void Rules_LeaveOutAnInvalidAlias(string origin)
    {
        var matcher = MatcherWithAliases([], ("broken", origin));

        Assert.Empty(matcher.Rules);
        Assert.False(matcher.ShouldRoute(new Uri("http://example.com/")));
    }

    [Fact]
    public void Rules_LeaveOutAnInvalidAliasButKeepTheValidOnes()
    {
        var matcher = MatcherWithAliases([], ("broken", "ftp://files.example.com"), ("orders", OrdersAlias));

        Assert.Equal(["10.0.0.5:8080"], RuleText(matcher));
    }

    [Fact]
    public void Rules_DoNotDuplicateAnAliasCoveredByAPortlessTarget()
    {
        var matcher = MatcherWithAliases(["10.0.0.5"], ("orders", OrdersAlias));

        Assert.Equal(["10.0.0.5"], RuleText(matcher));
        Assert.True(matcher.ShouldRoute(new Uri("http://10.0.0.5:8080/")));
        Assert.True(matcher.ShouldRoute(new Uri("http://10.0.0.5:8081/")));
    }

    [Fact]
    public void Rules_DoNotDuplicateAnAliasCoveredByAWildcardTarget()
    {
        var matcher = MatcherWithAliases(["*.example.com"], ("api", "https://api.example.com"));

        Assert.Equal(["*.example.com"], RuleText(matcher));
    }

    [Fact]
    public void Rules_DoNotDuplicateTwoAliasesForTheSameOrigin()
    {
        var matcher = MatcherWithAliases([], ("orders", OrdersAlias), ("orders2", OrdersAlias + "/"));

        Assert.Equal(["10.0.0.5:8080"], RuleText(matcher));
    }

    [Fact]
    public void Rules_AddAnAliasWhenATargetOnTheSameHostNamesAnotherPort()
    {
        var matcher = MatcherWithAliases(["10.0.0.5:9000"], ("orders", OrdersAlias));

        Assert.Equal(["10.0.0.5:9000", "10.0.0.5:8080"], RuleText(matcher));
    }

    [Fact]
    public void ShouldRoute_AppliesConfiguredTargetsAlongsideAliases()
    {
        var matcher = MatcherWithAliases(["example.com", "*.corp.test"], ("orders", OrdersAlias));

        Assert.Equal(["example.com", "*.corp.test", "10.0.0.5:8080"], RuleText(matcher));
        Assert.True(matcher.ShouldRoute(new Uri("http://example.com:9000/")));
        Assert.True(matcher.ShouldRoute(new Uri("https://app.corp.test/")));
        Assert.True(matcher.ShouldRoute(new Uri("http://10.0.0.5:8080/")));
        Assert.False(matcher.ShouldRoute(new Uri("http://10.0.0.5:8081/")));
        Assert.False(matcher.ShouldRoute(new Uri("http://other.test/")));
    }

    private const string UatEndpoint = "https://devconduit-uat.corp:8091";
    private const string ProdEndpoint = "https://devconduit-prod.corp:8091";

    /// <summary>
    /// Two remotes, uat the default: uat relays <c>*.uat.internal</c>, prod relays
    /// <c>*.prod.internal</c> and <c>*.shared.corp</c>. orders uses the default remote,
    /// orders-prod names prod, and api names uat although prod's Targets also match it.
    /// </summary>
    private static (DevConduitTargetMatcher Matcher, RoutingConfiguration Routing) TwoRemoteMatcher(
        Action<DevConduitOptions>? configure = null,
        params (string Key, string? Value)[] extra)
    {
        var routing = TestOptions.ParseRouting(
        [
            ("Remotes:uat:Endpoint", UatEndpoint),
            ("Remotes:uat:Targets:0", "*.uat.internal"),
            ("Remotes:prod:Endpoint", ProdEndpoint),
            ("Remotes:prod:Targets:0", "*.prod.internal"),
            ("Remotes:prod:Targets:1", "*.shared.corp"),
            ("DefaultRemote", "uat"),
            ("Routes:orders", OrdersAlias),
            ("Routes:orders-prod:Target", "http://10.50.1.10:8080"),
            ("Routes:orders-prod:Remote", "prod"),
            ("Routes:api:Target", "https://api.shared.corp"),
            ("Routes:api:Remote", "uat"),
            .. extra
        ]);

        Assert.Empty(routing.Problems);
        var matcher = new DevConduitTargetMatcher(
            routing,
            new RouteAliasTable(routing),
            TestOptions.Create(configure),
            NullLogger<DevConduitTargetMatcher>.Instance);

        return (matcher, routing);
    }

    [Theory]
    [InlineData("http://10.0.0.5:8080/api", "uat")]
    [InlineData("http://10.50.1.10:8080/api", "prod")]
    [InlineData("https://orders.prod.internal/", "prod")]
    [InlineData("https://orders.uat.internal:4443/", "uat")]
    [InlineData("https://other.shared.corp/", "prod")]
    public void TryMatch_RelaysEachMatchingHostToItsOwnRemote(string url, string expected)
    {
        var (matcher, _) = TwoRemoteMatcher();

        Assert.True(matcher.TryMatch(new Uri(url), out var remote));
        Assert.Equal(expected, remote.Name);
    }

    [Fact]
    public void TryMatch_PrefersARouteOverAnotherRemotesTargets()
    {
        var (matcher, _) = TwoRemoteMatcher();

        Assert.True(matcher.TryMatch(new Uri("https://api.shared.corp/v1"), out var remote));
        Assert.Equal("uat", remote.Name);

        // Off the route's port, only prod's Targets rule applies.
        Assert.True(matcher.TryMatch(new Uri("http://api.shared.corp/v1"), out var other));
        Assert.Equal("prod", other.Name);
    }

    [Theory]
    [InlineData("http://unlisted.example.com/")]
    [InlineData("http://10.50.1.10:8081/")]
    public void TryMatch_NeverFallsBackToTheDefaultRemote(string url)
    {
        var (matcher, routing) = TwoRemoteMatcher();
        Assert.NotNull(routing.DefaultRemote);

        Assert.False(matcher.TryMatch(new Uri(url), out var remote));
        Assert.Null(remote);
        Assert.False(matcher.TryFindRemote(new Uri(url), out _));
    }

    [Theory]
    [InlineData(UatEndpoint + "/route/api")]
    [InlineData(ProdEndpoint + "/route/api")]
    public void TryMatch_RefusesToLoopToAnyRemoteEndpoint(string url)
    {
        var (matcher, _) = TwoRemoteMatcher(null, ("Remotes:uat:Targets:1", "*.corp"));

        // The rule covers the endpoint host, so only the loop guard keeps it from relaying.
        Assert.True(matcher.TryFindRemote(new Uri(url), out _));
        Assert.False(matcher.TryMatch(new Uri(url), out var remote));
        Assert.Null(remote);
    }

    [Fact]
    public void TryMatch_RelaysARemoteEndpointHostOnAnotherPort()
    {
        var (matcher, _) = TwoRemoteMatcher(null, ("Remotes:uat:Targets:1", "*.corp"));

        Assert.True(matcher.TryMatch(new Uri("https://devconduit-prod.corp:9443/"), out var remote));
        Assert.Equal("uat", remote.Name);
    }

    [Theory]
    [InlineData("http://localhost:9100/", false)]
    [InlineData("http://127.0.0.1:9101/", false)]
    [InlineData("http://[::1]:9100/", false)]
    [InlineData("http://localhost:8092/", true)]
    [InlineData("http://127.0.0.1:8093/", true)]
    public void TryMatch_RefusesToLoopToItsOwnConfiguredPorts(string url, bool expected)
    {
        var (matcher, _) = TwoRemoteMatcher(
            options =>
            {
                options.Local.Port = 9100;
                options.Local.ForwardProxyPort = 9101;
            },
            ("Remotes:uat:Targets:1", "localhost"),
            ("Remotes:uat:Targets:2", "127.0.0.1"),
            ("Remotes:uat:Targets:3", "[::1]"));

        Assert.Equal(expected, matcher.TryMatch(new Uri(url), out _));
    }

    [Fact]
    public void RemoteRules_PairEachRuleWithItsRemoteAndRoute()
    {
        var (matcher, _) = TwoRemoteMatcher();

        var rules = matcher.RemoteRules
            .Select(rule => $"{rule.Rule}>{rule.Remote.Name}>{rule.Route ?? "-"}")
            .ToArray();

        Assert.Equal(
        [
            "*.prod.internal>prod>-",
            "*.shared.corp>prod>-",
            "*.uat.internal>uat>-",
            "api.shared.corp:443>uat>api",
            "10.0.0.5:8080>uat>orders",
            "10.50.1.10:8080>prod>orders-prod"
        ],
            rules);
    }
}
