using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Endpoints;
using Proxylayer.DevConduit.Models;
using Proxylayer.DevConduit.Services;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Services;

/// <summary>
/// <c>DevConduit:Route:AllowedTargets</c>: parsing each entry, matching the target a request
/// names, matching the addresses a connection resolves to, the route targets allowed besides,
/// and the 403 <c>/route</c> answers with. Only loopback sockets are opened.
/// </summary>
public sealed class TargetAllowListTests
{
    private static TargetAllowList List(params string[] entries) => new(entries, []);

    private static IPAddress[] Addresses(params string[] addresses) => [.. addresses.Select(IPAddress.Parse)];

    // ---- Parsing -------------------------------------------------------------------------

    [Theory]
    [InlineData("api.internal.example", "api.internal.example")]
    [InlineData(" API.Internal.Example. ", "api.internal.example")]
    [InlineData("api.internal.example:8443", "api.internal.example:8443")]
    [InlineData("*.internal.example", "*.internal.example")]
    [InlineData("*.internal.example:443", "*.internal.example:443")]
    [InlineData("10.0.0.5", "10.0.0.5/32")]
    [InlineData("10.0.0.5:8080", "10.0.0.5/32:8080")]
    [InlineData("10.0.0.0/16", "10.0.0.0/16")]
    [InlineData("10.0.0.0/16:8080", "10.0.0.0/16:8080")]
    [InlineData("fd00::/8", "fd00::/8")]
    [InlineData("[fd00::/8]:443", "[fd00::/8]:443")]
    [InlineData("fd00::1", "fd00::1/128")]
    [InlineData("[fd00::1]:8443", "[fd00::1/128]:8443")]
    [InlineData("localhost", "localhost")]
    public void TryParse_AcceptsEachForm(string entry, string normalized)
    {
        Assert.True(AllowedTarget.TryParse(entry, out var target, out var error), error);
        Assert.Equal(normalized, target.ToString());
    }

    [Theory]
    [InlineData("https://api.internal.example", "without a scheme")]
    [InlineData("api.internal.example/path", "without a scheme or path")]
    [InlineData("api internal", "without a scheme")]
    [InlineData("api.internal.example:0", "unusable port")]
    [InlineData("api.internal.example:70000", "unusable port")]
    [InlineData("api.internal.example:http", "unusable port")]
    [InlineData("[fd00::1", "unusable port")]
    [InlineData("10.0.1.0/16", "is not a CIDR network")]
    [InlineData("10.0.0.0/33", "is not a CIDR network")]
    [InlineData("10.19/16", "is not a CIDR network")]
    [InlineData("10.19", "is not a full IP address")]
    [InlineData("*", "is not a usable wildcard")]
    [InlineData("*.", "is not a usable wildcard")]
    [InlineData("api.*.example", "is not a usable wildcard")]
    [InlineData("*api.example", "is not a usable wildcard")]
    [InlineData("[api.example]:80", "is not a host name")]
    [InlineData("under_score.example", "is not a host name")]
    public void TryParse_RefusesAMalformedEntrySayingWhy(string entry, string expected)
    {
        Assert.False(AllowedTarget.TryParse(entry, out var target, out var error));
        Assert.Null(target);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_ReportsMalformedEntriesAndKeepsTheRest()
    {
        var list = List("api.internal.example", "https://web.internal", "10.0.1.0/16", "10.0.0.0/16");

        Assert.Equal(["api.internal.example", "10.0.0.0/16"], list.Entries.Select(entry => entry.ToString()));
        Assert.Equal(2, list.Problems.Count);
        Assert.All(list.Problems, problem => Assert.EndsWith("The entry was ignored.", problem, StringComparison.Ordinal));
    }

    // ---- Empty and blank lists -----------------------------------------------------------

    [Fact]
    public void AnEmptyList_AllowsEverything()
    {
        var list = List();

        Assert.False(list.IsRestricted);
        Assert.True(list.Allows(new Uri("http://anything.example:1234")));
        Assert.True(list.AllowsConnection("anything.example", 1234, Addresses("203.0.113.9")));
        Assert.False(TargetAllowList.Unrestricted.IsRestricted);
    }

    [Fact]
    public void BlankEntries_AreIgnoredLikeThePaddingOfAnyListSetting()
    {
        var list = new TargetAllowList(["", "  ", null], []);

        Assert.False(list.IsRestricted);
        Assert.Empty(list.Problems);
    }

    [Fact]
    public void AListOfOnlyMalformedEntries_StillRestricts()
    {
        var list = new TargetAllowList(["https://api.internal.example"], [new Uri("http://10.0.0.5:8080")]);

        Assert.True(list.IsRestricted);
        Assert.False(list.Allows(new Uri("https://api.internal.example")));
        Assert.True(list.Allows(new Uri("http://10.0.0.5:8080")));
    }

    // ---- The target a request names ------------------------------------------------------

    [Theory]
    [InlineData("http://api.internal.example", true)]
    [InlineData("https://API.INTERNAL.EXAMPLE:8443", true)]
    [InlineData("http://web.internal.example", false)]
    [InlineData("http://api.internal.example.evil.test", false)]
    public void Allows_MatchesAnExactHostName(string origin, bool allowed) =>
        Assert.Equal(allowed, List("api.internal.example").Allows(new Uri(origin)));

    [Theory]
    [InlineData("https://api.internal.example:8443", true)]
    [InlineData("https://api.internal.example", false)]
    [InlineData("http://api.internal.example:8443", true)]
    public void Allows_HoldsAnEntryToItsPort(string origin, bool allowed) =>
        Assert.Equal(allowed, List("api.internal.example:8443").Allows(new Uri(origin)));

    [Theory]
    [InlineData("http://a.internal.example", true)]
    [InlineData("http://a.b.internal.example", true)]
    [InlineData("http://internal.example", false)]
    [InlineData("http://ainternal.example", false)]
    [InlineData("http://a.internal.example.evil.test", false)]
    public void Allows_MatchesAWildcardBelowTheDomainOnly(string origin, bool allowed) =>
        Assert.Equal(allowed, List("*.internal.example").Allows(new Uri(origin)));

    [Theory]
    [InlineData("http://10.0.0.5:8080", true)]
    [InlineData("http://10.0.0.1", true)]
    [InlineData("http://10.20.0.1", false)]
    [InlineData("http://[::ffff:10.0.0.1]", true)]
    [InlineData("http://[fd00::5]:443", true)]
    [InlineData("http://[fe00::5]:443", false)]
    public void Allows_MatchesAnAddressLiteralAgainstTheNetworks(string origin, bool allowed) =>
        Assert.Equal(allowed, List("10.0.0.0/16", "fd00::/8").Allows(new Uri(origin)));

    [Fact]
    public void Allows_LeavesAHostNameToTheConnectCheckWhenANetworkMightCoverIt()
    {
        Assert.True(List("10.0.0.0/16").Allows(new Uri("http://cases.internal.example:8080")));
        Assert.False(List("10.0.0.0/16:443").Allows(new Uri("http://cases.internal.example:8080")));
        Assert.False(List("api.internal.example").Allows(new Uri("http://cases.internal.example")));
    }

    [Fact]
    public void Allows_EveryRouteTargetImplicitly()
    {
        var routes = new RouteAliasTable(TestOptions.Routing(("orders", "http://10.0.0.5:8080"), ("web", "https://web.internal")));
        var list = TargetAllowList.For(new DevConduitOptions { Route = { AllowedTargets = ["api.internal.example"] } }, routes);

        Assert.True(list.Allows(new Uri("http://10.0.0.5:8080")));
        Assert.True(list.Allows(new Uri("https://WEB.internal")));
        Assert.False(list.Allows(new Uri("http://10.0.0.5:8081")));
        Assert.False(list.Allows(new Uri("http://web.internal")));
        Assert.True(list.AllowsConnection("web.internal", 443, Addresses("203.0.113.5")));
    }

    [Fact]
    public void For_NeverRestrictsLocalMode()
    {
        var options = new DevConduitOptions { Mode = DevConduitMode.Local, Route = { AllowedTargets = ["api.internal.example"] } };

        Assert.Same(TargetAllowList.Unrestricted, TargetAllowList.For(options, new RouteAliasTable(TestOptions.Routing())));
    }

    // ---- The connection --------------------------------------------------------------------

    [Fact]
    public void AllowsConnection_TrustsAHostNameEntryWhateverItResolvesTo() =>
        Assert.True(List("api.internal.example").AllowsConnection("api.internal.example", 80, Addresses("203.0.113.5")));

    [Fact]
    public void AllowsConnection_NeedsEveryResolvedAddressInsideANetwork()
    {
        var list = List("10.0.0.0/16");

        Assert.True(list.AllowsConnection("cases.internal.example", 8080, Addresses("10.0.1.1", "10.0.2.2")));
        Assert.False(list.AllowsConnection("cases.internal.example", 8080, Addresses("10.0.1.1", "203.0.113.5")));
        Assert.False(list.AllowsConnection("cases.internal.example", 8080, Addresses("203.0.113.5")));
        Assert.False(list.AllowsConnection("cases.internal.example", 8080, []));
    }

    [Fact]
    public void AllowsConnection_DoesNotLetAnAddressLiteralPassAsAHostName() =>
        Assert.False(List("api.internal.example").AllowsConnection("10.0.0.1", 80, Addresses("10.0.0.1")));

    [Fact]
    public void EnsureConnectionAllowed_NamesTheHostItsAddressesAndTheSetting()
    {
        var exception = Assert.Throws<TargetNotAllowedException>(() =>
            List("10.0.0.0/16").EnsureConnectionAllowed("cases.internal.example", 8080, Addresses("203.0.113.5")));

        Assert.Equal("cases.internal.example", exception.Host);
        Assert.Equal(8080, exception.Port);
        Assert.Contains("'cases.internal.example:8080' (resolved to 203.0.113.5)", exception.Message, StringComparison.Ordinal);
        Assert.Contains(TargetAllowList.SettingKey, exception.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<IOException>(exception);
    }

    [Fact]
    public async Task ConnectGuardedAsync_RefusesALiteralOutsideTheListWithoutConnecting()
    {
        var guard = new EgressNetworkGuard(new DevConduitOptions().BlockedEgressNetworks);

        var exception = await Assert.ThrowsAsync<TargetNotAllowedException>(async () =>
            await ProxyHttpClients.ConnectGuardedAsync(guard, List("10.0.0.0/16"), new DnsEndPoint("127.0.0.1", 9), TestContext.Current.CancellationToken));

        Assert.Equal("127.0.0.1", exception.Host);
    }

    [Fact]
    public async Task ConnectGuardedAsync_ChecksTheAddressesANameResolvesTo()
    {
        // localhost resolves to loopback addresses only: inside 127.0.0.0/8 and ::1, outside 10.0.0.0/16.
        var guard = new EgressNetworkGuard(new DevConduitOptions().BlockedEgressNetworks);

        await Assert.ThrowsAsync<TargetNotAllowedException>(async () =>
            await ProxyHttpClients.ConnectGuardedAsync(guard, List("10.0.0.0/16"), new DnsEndPoint("localhost", 9), TestContext.Current.CancellationToken));

        var cancellationToken = TestContext.Current.CancellationToken;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accept = listener.AcceptTcpClientAsync(cancellationToken);

            await using var stream = await ProxyHttpClients.ConnectGuardedAsync(
                guard, List("127.0.0.0/8", "::1"), new DnsEndPoint("localhost", port), cancellationToken);
            using var server = await accept;

            Assert.True(stream.CanWrite);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ConnectGuardedAsync_StillRefusesABlockedNetworkTheListAllows()
    {
        var guard = new EgressNetworkGuard(new DevConduitOptions().BlockedEgressNetworks);

        await Assert.ThrowsAsync<EgressBlockedException>(async () =>
            await ProxyHttpClients.ConnectGuardedAsync(
                guard, List("169.254.0.0/16"), new DnsEndPoint("169.254.169.254", 80), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Classify_ReportsAConnectTimeRefusalAsTargetNotAllowed()
    {
        var refused = new TargetNotAllowedException("cases.internal.example", 80, Addresses("203.0.113.5"));

        var (kind, message) = HttpFailureClassifier.Classify(new HttpRequestException("x", new IOException("wrapped", refused)));

        Assert.Equal(ProxyErrorKind.TargetNotAllowed, kind);
        Assert.Equal(refused.Message, message);
    }

    // ---- /route ----------------------------------------------------------------------------

    [Fact]
    public async Task Route_RefusesATargetTheListDoesNotCoverWith403()
    {
        var context = await RouteAsync("http://evil.example:8080", "api.internal.example");

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        var failure = ReadFailure(context);
        Assert.Equal("target_not_allowed", failure.ErrorKind);
        Assert.Contains("'http://evil.example:8080' is not covered by DevConduit:Route:AllowedTargets", failure.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void UpstreamRouteEgress_RefusesOnlyWhatTheListDoesNotCover()
    {
        var egress = new UpstreamRouteEgress(List("api.internal.example"));

        Assert.Null(egress.Refuse(new RouteTarget(new Uri("http://api.internal.example"), null, PathString.Empty)));
        var refusal = egress.Refuse(new RouteTarget(new Uri("http://evil.example"), null, PathString.Empty));
        Assert.NotNull(refusal);
        Assert.Equal(TargetAllowList.ErrorKind, refusal.Kind);
        Assert.Equal(StatusCodes.Status403Forbidden, refusal.Status);
    }

    [Fact]
    public async Task Route_AnswersOtherRefusalsWith400AsBefore()
    {
        var context = await RouteAsync("not a uri", "api.internal.example");

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("invalid_target", ReadFailure(context).ErrorKind);
    }

    /// <summary>Runs the real /route branch up to the point it would forward, with every caller authorized.</summary>
    private static async Task<DefaultHttpContext> RouteAsync(string target, params string[] allowed)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TestOptions.Create());
        services.AddSingleton(new RouteAliasTable(TestOptions.Routing()));
        services.AddSingleton<RouteTargetResolver>();
        services.AddSingleton<IRouteAuthorizer, AllowEveryone>();
        services.AddSingleton<IRouteEgress>(new UpstreamRouteEgress(List(allowed)));
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();

        var app = new ApplicationBuilder(provider);
        app.MapDevConduitRoute();
        var pipeline = app.Build();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Method = "GET";
        context.Request.Path = "/route/api/cases";
        context.Request.Headers[DevConduitHeaderNames.Target] = target;
        context.Response.Body = new MemoryStream();

        await pipeline(context);
        return context;
    }

    private static RouteFailure ReadFailure(HttpContext context) =>
        JsonSerializer.Deserialize<RouteFailure>(
            Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private sealed class AllowEveryone : IRouteAuthorizer
    {
        public Task<bool> TryAuthorizeAsync(HttpContext context) => Task.FromResult(true);
    }
}
