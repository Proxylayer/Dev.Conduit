using Microsoft.AspNetCore.Http;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Services;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Services;

/// <summary>Where a /route call is pointed, and how the upstream URI is composed.</summary>
public sealed class RouteTargetResolverTests
{
    private static RouteTargetResolver Resolver(Action<DevConduitOptions> configure, params (string Name, string Target)[] routes)
    {
        var options = TestOptions.Create(configure);
        return new RouteTargetResolver(new RouteAliasTable(TestOptions.Routing(routes)), options);
    }

    private static RouteTargetResolver WithAlias(string name = "orders", string origin = "http://10.0.0.5:8080") =>
        Resolver(_ => { }, (name, origin));

    private static HttpRequest Request(string path, string? query = null, string? targetHeader = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = new PathString(path);
        if (query is not null)
        {
            context.Request.QueryString = new QueryString(query);
        }

        if (targetHeader is not null)
        {
            context.Request.Headers[RouteTargetResolver.TargetHeader] = targetHeader;
        }

        return context.Request;
    }

    [Fact]
    public void TryResolve_PrefersALeadingAliasSegment()
    {
        Assert.True(WithAlias().TryResolve(Request("/orders/api/cases"), out var target, out _));

        Assert.Equal("orders", target.Alias);
        Assert.Equal("http://10.0.0.5:8080", target.Origin.GetLeftPart(UriPartial.Authority));
        Assert.Equal("/api/cases", target.RemainingPath.Value);
    }

    [Fact]
    public void TryResolve_LeavesAnEmptyRemainderWhenTheAliasIsTheWholePath()
    {
        Assert.True(WithAlias().TryResolve(Request("/orders"), out var target, out _));

        Assert.Equal("orders", target.Alias);
        Assert.False(target.RemainingPath.HasValue);
    }

    [Fact]
    public void TryResolve_MatchesAnAliasWithoutRegardToCase()
    {
        Assert.True(WithAlias().TryResolve(Request("/ORDERS/api"), out var target, out _));
        Assert.Equal("ORDERS", target.Alias);
    }

    [Fact]
    public void TryResolve_PrefersTheAliasOverTheTargetHeader()
    {
        var resolved = WithAlias().TryResolve(
            Request("/orders/api", targetHeader: "http://elsewhere.example:9000"),
            out var target,
            out _);

        Assert.True(resolved);
        Assert.Equal("orders", target.Alias);
        Assert.Equal("http://10.0.0.5:8080", target.Origin.GetLeftPart(UriPartial.Authority));
    }

    [Fact]
    public void TryResolve_FallsBackToTheTargetHeaderAndKeepsTheWholePath()
    {
        var resolved = WithAlias().TryResolve(
            Request("/api/cases", targetHeader: "http://elsewhere.example:9000"),
            out var target,
            out _);

        Assert.True(resolved);
        Assert.Null(target.Alias);
        Assert.Equal("http://elsewhere.example:9000", target.Origin.GetLeftPart(UriPartial.Authority));
        Assert.Equal("/api/cases", target.RemainingPath.Value);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/unknown/path")]
    public void TryResolve_ReportsNoTargetWhenNothingNamesAnUpstream(string path)
    {
        Assert.False(WithAlias().TryResolve(Request(path), out _, out var failure));

        Assert.Equal("no_target", failure.Kind);
        Assert.Contains(RouteTargetResolver.TargetHeader, failure.Message);
        Assert.Contains("orders", failure.Message);
    }

    [Fact]
    public void TryResolve_SaysSoWhenNoAliasesAreConfigured()
    {
        Assert.False(Resolver(_ => { }).TryResolve(Request("/api"), out _, out var failure));

        Assert.Equal("no_target", failure.Kind);
        Assert.Contains("(none are configured)", failure.Message);
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("http://example.com/path")]
    [InlineData("/not-absolute")]
    [InlineData("http://user:pw@example.com")]
    public void TryResolve_ReportsAnInvalidTargetHeader(string header)
    {
        Assert.False(WithAlias().TryResolve(Request("/api", targetHeader: header), out _, out var failure));

        Assert.Equal("invalid_target", failure.Kind);
        Assert.Contains(RouteTargetResolver.TargetHeader, failure.Message);
    }

    [Fact]
    public void TryResolve_RefusesTheTargetHeaderWhenOnlyAliasesAreAllowed()
    {
        var resolver = Resolver(
            options => options.Route.AliasesOnly = true,
            ("orders", "http://10.0.0.5:8080"));

        Assert.False(resolver.TryResolve(Request("/api", targetHeader: "http://elsewhere.example"), out _, out var failure));

        Assert.Equal("aliases_only", failure.Kind);
    }

    [Fact]
    public void TryResolve_StillAcceptsAnAliasWhenOnlyAliasesAreAllowed()
    {
        var resolver = Resolver(
            options => options.Route.AliasesOnly = true,
            ("orders", "http://10.0.0.5:8080"));

        Assert.True(resolver.TryResolve(Request("/orders/api"), out var target, out _));
        Assert.Equal("orders", target.Alias);
    }

    [Fact]
    public void TryResolve_ReportsNoTargetBeforeAliasesOnlyWhenTheHeaderIsAbsent()
    {
        var resolver = Resolver(
            options => options.Route.AliasesOnly = true,
            ("orders", "http://10.0.0.5:8080"));

        Assert.False(resolver.TryResolve(Request("/api"), out _, out var failure));
        Assert.Equal("no_target", failure.Kind);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void TryResolve_TreatsABlankTargetHeaderAsAbsent(string header)
    {
        Assert.False(WithAlias().TryResolve(Request("/api", targetHeader: header), out _, out var failure));
        Assert.Equal("no_target", failure.Kind);
    }

    [Fact]
    public void BuildUpstreamUri_JoinsTheOriginToTheRemainingPathAndQuery()
    {
        var target = new RouteTarget(new Uri("http://10.0.0.5:8080"), "orders", new PathString("/api/cases"));

        var uri = RouteTargetResolver.BuildUpstreamUri(target, new QueryString("?page=2&q=a%20b"));

        Assert.Equal("http://10.0.0.5:8080/api/cases?page=2&q=a%20b", uri.AbsoluteUri);
    }

    [Fact]
    public void BuildUpstreamUri_UsesTheRootWhenNothingRemains()
    {
        var target = new RouteTarget(new Uri("http://10.0.0.5:8080"), "orders", PathString.Empty);

        Assert.Equal("http://10.0.0.5:8080/", RouteTargetResolver.BuildUpstreamUri(target, QueryString.Empty).AbsoluteUri);
    }

    [Fact]
    public void BuildUpstreamUri_NeverRepeatsThePort()
    {
        var target = new RouteTarget(new Uri("http://10.0.0.5:8080"), "orders", new PathString("/api"));

        var uri = RouteTargetResolver.BuildUpstreamUri(target, QueryString.Empty);

        Assert.Equal(8080, uri.Port);
        Assert.Equal("http://10.0.0.5:8080/api", uri.AbsoluteUri);
        Assert.Equal(2, uri.AbsoluteUri.Split(":8080").Length);
    }

    [Fact]
    public void BuildUpstreamUri_PreservesTheQueryVerbatim()
    {
        var target = new RouteTarget(new Uri("https://example.com"), null, new PathString("/search"));

        var uri = RouteTargetResolver.BuildUpstreamUri(target, new QueryString("?a=1&a=2&empty=&flag&enc=%2Fslash%2F"));

        Assert.Equal("?a=1&a=2&empty=&flag&enc=%2Fslash%2F", uri.Query);
    }

    [Fact]
    public void BuildUpstreamUri_EscapesThePathItWasGiven()
    {
        var target = new RouteTarget(new Uri("https://example.com"), null, new PathString("/a b/c"));

        Assert.Equal("https://example.com/a%20b/c", RouteTargetResolver.BuildUpstreamUri(target, QueryString.Empty).AbsoluteUri);
    }

    [Fact]
    public void TargetHeader_IsTheDocumentedName() =>
        Assert.Equal("X-DevConduit-Target", RouteTargetResolver.TargetHeader);
}
