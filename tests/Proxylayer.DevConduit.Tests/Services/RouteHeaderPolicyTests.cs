using Microsoft.Extensions.Primitives;
using Proxylayer.DevConduit.Endpoints;
using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Tests.Services;

/// <summary>The header sets that decide what crosses the proxy boundary.</summary>
public sealed class RouteHeaderPolicyTests
{
    [Theory]
    [InlineData("Connection")]
    [InlineData("connection")]
    [InlineData("Keep-Alive")]
    [InlineData("Proxy-Authenticate")]
    [InlineData("Proxy-Authorization")]
    [InlineData("TE")]
    [InlineData("Trailer")]
    [InlineData("Transfer-Encoding")]
    [InlineData("transfer-encoding")]
    [InlineData("Upgrade")]
    public void HopByHop_CoversTheConnectionScopedHeaders(string name) =>
        Assert.Contains(name, RouteHeaderPolicy.HopByHop);

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Host")]
    [InlineData("Accept")]
    [InlineData("Content-Length")]
    public void HopByHop_DoesNotCoverEndToEndHeaders(string name) =>
        Assert.DoesNotContain(name, RouteHeaderPolicy.HopByHop);

    [Theory]
    [InlineData("X-DevConduit-Target")]
    [InlineData("x-devconduit-target")]
    [InlineData("X-DevConduit-Client")]
    [InlineData("X-DevConduit-Upstream-Authorization")]
    [InlineData("X-DevConduit-Local-Secret")]
    [InlineData("x-devconduit-local-secret")]
    public void Control_CoversTheHeadersDevConduitConsumesItself(string name) =>
        Assert.Contains(name, RouteHeaderPolicy.Control);

    [Fact]
    public void Control_IsBuiltFromTheDeclaredHeaderNames()
    {
        Assert.Contains(RouteTargetResolver.TargetHeader, RouteHeaderPolicy.Control);
        Assert.Contains(ClientHeaderFilter.HeaderName, RouteHeaderPolicy.Control);
        Assert.Contains(RouteHeaderPolicy.UpstreamAuthorizationHeader, RouteHeaderPolicy.Control);
        Assert.Contains(DevConduitHeaderNames.LocalSecret, RouteHeaderPolicy.Control);
        Assert.Equal("X-DevConduit-Upstream-Authorization", RouteHeaderPolicy.UpstreamAuthorizationHeader);
        Assert.Equal("Authorization", RouteHeaderPolicy.AuthorizationHeader);
    }

    [Fact]
    public void Control_DoesNotCoverTheDevConduitCredentialItself()
    {
        // Authorization is dropped by an explicit rule rather than by the control set,
        // because the same name is what the upstream credential is renamed to.
        Assert.DoesNotContain(RouteHeaderPolicy.AuthorizationHeader, RouteHeaderPolicy.Control);
    }

    [Fact]
    public void ConnectionTokens_ExpandsACommaSeparatedList()
    {
        var tokens = RouteHeaderPolicy.ConnectionTokens(new StringValues("keep-alive, X-Custom-Hop , Upgrade"));

        Assert.Equal(3, tokens.Count);
        Assert.Contains("keep-alive", tokens);
        Assert.Contains("X-Custom-Hop", tokens);
        Assert.Contains("Upgrade", tokens);
    }

    [Fact]
    public void ConnectionTokens_MatchesWithoutRegardToCase()
    {
        var tokens = RouteHeaderPolicy.ConnectionTokens(new StringValues("X-Custom-Hop"));

        Assert.Contains("x-custom-hop", tokens);
        Assert.Contains("X-CUSTOM-HOP", tokens);
    }

    [Fact]
    public void ConnectionTokens_MergesSeveralConnectionHeaderValues()
    {
        var tokens = RouteHeaderPolicy.ConnectionTokens(new StringValues(["close", "X-One , X-Two"]));

        Assert.Equal(3, tokens.Count);
        Assert.Contains("close", tokens);
        Assert.Contains("X-One", tokens);
        Assert.Contains("X-Two", tokens);
    }

    [Fact]
    public void ConnectionTokens_IgnoresEmptyAndBlankEntries()
    {
        var tokens = RouteHeaderPolicy.ConnectionTokens(new StringValues(["", "  ", null, "close,, ,"]));

        Assert.Single(tokens);
        Assert.Contains("close", tokens);
    }

    [Fact]
    public void ConnectionTokens_IsEmptyWhenNoConnectionHeaderWasSent() =>
        Assert.Empty(RouteHeaderPolicy.ConnectionTokens(StringValues.Empty));
}
