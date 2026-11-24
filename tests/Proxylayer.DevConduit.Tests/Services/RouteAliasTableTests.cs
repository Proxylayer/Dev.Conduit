using Proxylayer.DevConduit.Services;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Services;

/// <summary>The configured alias map, and what happens to entries that cannot be used.</summary>
public sealed class RouteAliasTableTests
{
    private static RouteAliasTable Table(params (string Name, string Value)[] aliases) =>
        new(TestOptions.Routing(aliases));

    [Fact]
    public void TryResolve_FindsAConfiguredAlias()
    {
        var table = Table(("orders", "http://10.0.0.5:8080"));

        Assert.True(table.TryResolve("orders", out var origin));
        Assert.Equal("http://10.0.0.5:8080", origin!.GetLeftPart(UriPartial.Authority));
        Assert.Empty(table.Problems);
    }

    [Theory]
    [InlineData("ORDERS")]
    [InlineData("orders")]
    [InlineData("OrDeRs")]
    public void TryResolve_IgnoresCase(string lookup)
    {
        Assert.True(Table(("orders", "http://example.com")).TryResolve(lookup, out _));
    }

    [Fact]
    public void TryResolve_ReturnsFalseForAnUnknownName()
    {
        Assert.False(Table(("orders", "http://example.com")).TryResolve("other", out var origin));
        Assert.Null(origin);
    }

    [Fact]
    public void Constructor_TrimsTheAliasName()
    {
        var table = Table(("  padded  ", "http://example.com"));

        Assert.True(table.TryResolve("padded", out _));
        Assert.Contains("padded", table.Names);
    }

    [Theory]
    [InlineData("with/slash")]
    [InlineData("with\\backslash")]
    [InlineData("../escape")]
    public void Constructor_DropsAnAliasThatIsNotASinglePathSegment(string name)
    {
        var table = Table((name, "http://example.com"));

        Assert.Empty(table.Names);
        Assert.Contains(table.Problems, problem => problem.Contains("single path segment"));
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("http://example.com/path")]
    [InlineData("not a uri")]
    [InlineData("")]
    public void Constructor_DropsAnAliasWithAnUnusableOrigin(string value)
    {
        var table = Table(("bad", value));

        Assert.Empty(table.Names);
        Assert.Contains(table.Problems, problem => problem.StartsWith("Alias 'bad' was ignored:"));
    }

    [Fact]
    public void Constructor_ReportsRatherThanThrowsOnAMixOfGoodAndBadEntries()
    {
        var table = Table(
            ("good", "http://example.com"),
            ("bad", "ftp://example.com"),
            ("worse", "nonsense"));

        Assert.True(table.TryResolve("good", out _));
        Assert.Single(table.Names);
        Assert.Equal(2, table.Problems.Count);
    }

    [Fact]
    public void Constructor_ReadsOnlyEachRoutesNameAndTargetFromTheRoutingConfiguration()
    {
        // Server mode: no remotes at all, and routes that name remotes it never uses.
        var routing = TestOptions.ParseRouting(
            ("Routes:orders", "http://10.0.0.5:8080"),
            ("Routes:orders-prod:Target", "http://10.50.1.10:8080"),
            ("Routes:orders-prod:Remote", "prod"),
            ("Routes:other:Target", "https://api.example.com"),
            ("Routes:other:Remote", "nowhere"));

        var table = new RouteAliasTable(routing);

        Assert.Empty(table.Problems);
        Assert.Equal(["orders", "orders-prod", "other"], table.Names);
        Assert.True(table.TryResolve("orders", out var orders));
        Assert.Equal("http://10.0.0.5:8080/", orders.AbsoluteUri);
        Assert.True(table.TryResolve("ORDERS-PROD", out var ordersProd));
        Assert.Equal("http://10.50.1.10:8080/", ordersProd.AbsoluteUri);
        Assert.True(table.TryResolve("other", out var other));
        Assert.Equal("https://api.example.com/", other.AbsoluteUri);
    }

    [Fact]
    public void Constructor_ReportsAnObjectRouteWithoutATarget()
    {
        var routing = TestOptions.ParseRouting(("Routes:orphan:Remote", "prod"));

        var table = new RouteAliasTable(routing);

        Assert.Empty(table.Names);
        Assert.StartsWith("Alias 'orphan' was ignored:", Assert.Single(table.Problems));
    }
}
