using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Models;
using Proxylayer.DevConduit.Services;
using Proxylayer.DevConduit.Services.Local;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Services.Local;

/// <summary>
/// The local-mode egress: where it addresses a relayed call and what it records about it.
/// Nothing is sent; the outbound message is only built.
/// </summary>
public sealed class RemoteRelayRouteEgressTests
{
    private const string ProdOrigin = "http://10.50.1.10:8080";

    private sealed class FixedTokenProvider : IDevConduitTokenProvider
    {
        public ValueTask<string> GetAccessTokenAsync(RemoteDefinition remote, CancellationToken cancellationToken) =>
            ValueTask.FromResult("token-" + remote.Name);
    }

    private static RemoteRelayRouteEgress Egress(bool withDefault = true)
    {
        var routing = TestOptions.ParseRouting(
        [
            ("Remotes:uat", "https://devconduit-uat.corp:8091"),
            ("Remotes:prod", "https://devconduit-prod.corp:8091"),
            ("Routes:orders-prod:Target", ProdOrigin),
            ("Routes:orders-prod:Remote", "prod"),
            .. withDefault ? [("DefaultRemote", (string?)"uat")] : Array.Empty<(string, string?)>()
        ]);

        var matcher = new DevConduitTargetMatcher(
            routing,
            new RouteAliasTable(routing),
            TestOptions.Create(),
            NullLogger<DevConduitTargetMatcher>.Instance);

        return new RemoteRelayRouteEgress(new RemoteRelay(routing, matcher, new FixedTokenProvider()));
    }

    private static async Task<(List<HeaderItem> Captured, HttpRequestMessage Message)> ApplyAsync(
        RemoteRelayRouteEgress egress,
        RouteTarget target,
        Uri upstream)
    {
        var destination = egress.Destination(target, upstream);
        var message = new HttpRequestMessage(HttpMethod.Get, destination);
        var captured = new List<HeaderItem>();

        await egress.ApplyAsync(
            new RouteEgressContext(new DefaultHttpContext().Request, message, target, upstream, destination, captured),
            TestContext.Current.CancellationToken);

        return (captured, message);
    }

    [Fact]
    public async Task ApplyAsync_CapturesTheChosenRemoteAsADisabledHeader()
    {
        var target = new RouteTarget(new Uri(ProdOrigin), "orders-prod", new PathString("/api/orders"));

        var (captured, message) = await ApplyAsync(Egress(), target, new Uri(ProdOrigin + "/api/orders"));
        using (message)
        {
            var remote = Assert.Single(captured, header => header.Name == DevConduitHeaderNames.Remote);
            Assert.Equal(new HeaderItem("X-DevConduit-Remote", "prod", Enabled: false), remote);

            // Recorded for the history only; the remote never receives it.
            Assert.False(message.Headers.Contains(DevConduitHeaderNames.Remote));
            Assert.Equal(["Bearer token-prod"], message.Headers.GetValues(DevConduitHeaderNames.Authorization));
        }
    }

    [Fact]
    public async Task ApplyAsync_CapturesTheDefaultRemoteForAnUnlistedOrigin()
    {
        var target = new RouteTarget(new Uri("http://unlisted.example.com"), null, PathString.Empty);

        var (captured, message) = await ApplyAsync(Egress(), target, new Uri("http://unlisted.example.com/x"));
        message.Dispose();

        Assert.Contains(new HeaderItem(DevConduitHeaderNames.Remote, "uat", Enabled: false), captured);
    }

    [Fact]
    public void Destination_AddressesTheChosenRemote()
    {
        var target = new RouteTarget(new Uri(ProdOrigin), null, PathString.Empty);

        var destination = Egress().Destination(target, new Uri(ProdOrigin + "/api?x=1"));

        Assert.Equal("https://devconduit-prod.corp:8091/route/api?x=1", destination.AbsoluteUri);
    }

    [Fact]
    public void Refuse_AnswersNoRemoteWhenNoRemoteCanCarryTheTarget()
    {
        var target = new RouteTarget(new Uri("http://unlisted.example.com"), null, PathString.Empty);

        var failure = Egress(withDefault: false).Refuse(target);

        Assert.Equal(RemoteRelay.NoRemoteKind, failure?.Kind);
    }

    [Fact]
    public void Refuse_AcceptsATargetARouteAssigns()
    {
        var target = new RouteTarget(new Uri(ProdOrigin), null, PathString.Empty);

        Assert.Null(Egress(withDefault: false).Refuse(target));
    }
}
