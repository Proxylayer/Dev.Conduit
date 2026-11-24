using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Services;
using Proxylayer.DevConduit.Services.Local;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Services.Local;

/// <summary>
/// Which remote carries a <c>/route</c> request: the alias's route, then a route origin,
/// then a remote's Targets, then the default, and otherwise none.
/// </summary>
public sealed class RemoteRelayTests
{
    private const string UatEndpoint = "https://devconduit-uat.corp:8091";
    private const string ProdEndpoint = "https://devconduit-prod.corp:8091";
    private const string OrdersUat = "http://10.0.0.5:8080";
    private const string OrdersProd = "http://10.50.1.10:8080";

    /// <summary>Hands out a token naming the remote it was asked for, and records each request.</summary>
    private sealed class RecordingTokenProvider : IDevConduitTokenProvider
    {
        public List<string> Requested { get; } = [];

        public ValueTask<string> GetAccessTokenAsync(RemoteDefinition remote, CancellationToken cancellationToken)
        {
            Requested.Add(remote.Name);
            return ValueTask.FromResult("token-" + remote.Name);
        }
    }

    /// <summary>
    /// Two remotes: uat relays <c>*.uat.internal</c>, prod relays <c>*.prod.internal</c> and
    /// <c>*.shared.corp</c>. The orders route uses the default remote, orders-prod names prod, and
    /// api names uat although its host also matches prod's Targets.
    /// </summary>
    private static (string Key, string? Value)[] TwoRemotes(bool withDefault) =>
    [
        ("Remotes:uat:Endpoint", UatEndpoint),
        ("Remotes:uat:Targets:0", "*.uat.internal"),
        ("Remotes:prod:Endpoint", ProdEndpoint),
        ("Remotes:prod:Targets:0", "*.prod.internal"),
        ("Remotes:prod:Targets:1", "*.shared.corp"),
        ("Routes:orders-prod:Target", OrdersProd),
        ("Routes:orders-prod:Remote", "prod"),
        ("Routes:api:Target", "https://api.shared.corp"),
        ("Routes:api:Remote", "uat"),
        .. withDefault ? [("DefaultRemote", (string?)"uat"), ("Routes:orders", OrdersUat)] : Array.Empty<(string, string?)>()
    ];

    private static RemoteRelay Relay(
        (string Key, string? Value)[] settings,
        IDevConduitTokenProvider? tokens = null)
    {
        var routing = TestOptions.ParseRouting(settings);
        Assert.Empty(routing.Problems);

        var matcher = new DevConduitTargetMatcher(
            routing,
            new RouteAliasTable(routing),
            TestOptions.Create(),
            NullLogger<DevConduitTargetMatcher>.Instance);

        return new RemoteRelay(routing, matcher, tokens ?? new RecordingTokenProvider());
    }

    private static RouteTarget Target(string origin, string? alias = null) =>
        new(new Uri(origin), alias, PathString.Empty);

    private static string Select(RemoteRelay relay, RouteTarget target)
    {
        Assert.True(relay.TrySelect(target, out var remote, out var failure));
        Assert.Null(failure);
        return remote.Name;
    }

    [Fact]
    public void TrySelect_UsesTheRemoteOfTheRouteTheAliasNamed() =>
        Assert.Equal("prod", Select(Relay(TwoRemotes(withDefault: true)), Target(OrdersProd, "orders-prod")));

    [Fact]
    public void TrySelect_UsesTheDefaultRemoteForAnAliasWhoseRouteNamesNone() =>
        Assert.Equal("uat", Select(Relay(TwoRemotes(withDefault: true)), Target(OrdersUat, "ORDERS")));

    [Fact]
    public void TrySelect_PrefersTheAliasOverAnEarlierRouteForTheSameOrigin()
    {
        // Both routes share an origin; by origin alone the first route, on uat, would win.
        var relay = Relay(
        [
            ("Remotes:uat", UatEndpoint),
            ("Remotes:prod", ProdEndpoint),
            ("DefaultRemote", "uat"),
            ("Routes:a-uat:Target", OrdersProd),
            ("Routes:a-uat:Remote", "uat"),
            ("Routes:b-prod:Target", OrdersProd),
            ("Routes:b-prod:Remote", "prod")
        ]);

        Assert.Equal("prod", Select(relay, Target(OrdersProd, "b-prod")));
        Assert.Equal("uat", Select(relay, Target(OrdersProd)));
    }

    [Fact]
    public void TrySelect_UsesTheRouteWhoseOriginMatchesWhenThereIsNoAlias() =>
        Assert.Equal("prod", Select(Relay(TwoRemotes(withDefault: true)), Target(OrdersProd)));

    [Fact]
    public void TrySelect_MatchesARouteOriginOnHostAndPortOnly()
    {
        var relay = Relay(TwoRemotes(withDefault: true));

        Assert.Equal("prod", Select(relay, Target("http://10.50.1.10:8080")));

        // Another port on the same host is not the route's origin, so the default carries it.
        Assert.Equal("uat", Select(relay, Target("http://10.50.1.10:8081")));
    }

    [Theory]
    [InlineData("https://orders.prod.internal", "prod")]
    [InlineData("http://orders.uat.internal:9000", "uat")]
    [InlineData("https://other.shared.corp", "prod")]
    public void TrySelect_UsesTheRemoteWhoseTargetsMatch(string origin, string expected) =>
        Assert.Equal(expected, Select(Relay(TwoRemotes(withDefault: false)), Target(origin)));

    [Fact]
    public void TrySelect_PrefersARouteOverAnotherRemotesTargets()
    {
        // api.shared.corp matches prod's *.shared.corp, but the api route sends it to uat.
        var relay = Relay(TwoRemotes(withDefault: false));

        Assert.Equal("uat", Select(relay, Target("https://api.shared.corp")));
        Assert.Equal("uat", Select(relay, Target("https://api.shared.corp", "api")));
    }

    [Fact]
    public void TrySelect_FallsBackToTheDefaultRemote() =>
        Assert.Equal("uat", Select(Relay(TwoRemotes(withDefault: true)), Target("http://unlisted.example.com")));

    [Fact]
    public void TrySelect_FallsBackToTheOnlyRemote() =>
        Assert.Equal("uat", Select(Relay([("Remotes:uat", UatEndpoint)]), Target("http://unlisted.example.com")));

    [Fact]
    public void TrySelect_RefusesWithNoRemoteWhenNothingMatchesAndThereIsNoDefault()
    {
        var relay = Relay(TwoRemotes(withDefault: false));

        Assert.False(relay.TrySelect(Target("http://unlisted.example.com:8080"), out var remote, out var failure));

        Assert.Null(remote);
        Assert.Equal(RemoteRelay.NoRemoteKind, failure.Kind);
        Assert.Equal("no_remote", failure.Kind);
        Assert.Contains("http://unlisted.example.com:8080", failure.Message);
        Assert.Contains("(prod, uat)", failure.Message);
        Assert.Contains("DevConduit:DefaultRemote", failure.Message);
    }

    [Fact]
    public void RelayUri_AddressesTheChosenRemotesRouteEndpoint()
    {
        var relay = Relay(TwoRemotes(withDefault: true));
        Assert.True(relay.TrySelect(Target(OrdersProd, "orders-prod"), out var remote, out _));

        var uri = RemoteRelay.RelayUri(remote, new Uri("http://10.50.1.10:8080/api/orders?id=1"));

        Assert.Equal("https://devconduit-prod.corp:8091/route/api/orders?id=1", uri.AbsoluteUri);
    }

    [Fact]
    public async Task PrepareAsync_AsksForATokenForTheChosenRemote()
    {
        var tokens = new RecordingTokenProvider();
        var relay = Relay(TwoRemotes(withDefault: true), tokens);
        Assert.True(relay.TrySelect(Target(OrdersProd), out var remote, out _));

        var credentials = await relay.PrepareAsync(
            remote,
            new Uri("http://10.50.1.10:8080/api"),
            inboundAuthorization: "Basic upstream",
            inboundUpstreamAuthorization: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(["prod"], tokens.Requested);
        Assert.Equal("Bearer token-prod", credentials.Authorization);
        Assert.Equal("http://10.50.1.10:8080", credentials.Target);
        Assert.Equal("Basic upstream", credentials.UpstreamAuthorization);
    }

    [Fact]
    public void Constructor_RefusesARemoteWithoutAUsableEndpoint()
    {
        var routing = TestOptions.ParseRouting(("Remotes:uat", "not a uri"));
        var matcher = new DevConduitTargetMatcher(
            routing,
            new RouteAliasTable(routing),
            TestOptions.Create(),
            NullLogger<DevConduitTargetMatcher>.Instance);

        var exception = Assert.Throws<InvalidOperationException>(
            () => new RemoteRelay(routing, matcher, new RecordingTokenProvider()));

        Assert.Contains("DevConduit:Remotes:uat", exception.Message);
    }
}
