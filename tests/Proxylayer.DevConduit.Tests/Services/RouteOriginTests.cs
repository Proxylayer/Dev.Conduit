using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Tests.Services;

/// <summary>Validation of an upstream origin: scheme and authority, nothing more.</summary>
public sealed class RouteOriginTests
{
    [Theory]
    [InlineData("http://example.com", "http://example.com")]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("http://example.com:8080", "http://example.com:8080")]
    [InlineData("http://example.com:8080/", "http://example.com:8080")]
    [InlineData("  http://example.com:8080  ", "http://example.com:8080")]
    [InlineData("HTTP://Example.COM:8080", "http://example.com:8080")]
    [InlineData("http://10.0.0.5:8080", "http://10.0.0.5:8080")]
    [InlineData("http://[2001:db8::1]:8080", "http://[2001:db8::1]:8080")]
    public void TryParse_AcceptsAnOrigin(string value, string expected)
    {
        Assert.True(RouteOrigin.TryParse(value, out var origin, out var error), error);
        Assert.Equal(expected, origin!.GetLeftPart(UriPartial.Authority));
    }

    [Fact]
    public void TryParse_DropsADefaultPortRatherThanRepeatingIt()
    {
        Assert.True(RouteOrigin.TryParse("http://example.com:80", out var origin, out _));
        Assert.Equal("http://example.com", origin!.GetLeftPart(UriPartial.Authority));

        Assert.True(RouteOrigin.TryParse("https://example.com:443", out var secure, out _));
        Assert.Equal("https://example.com", secure!.GetLeftPart(UriPartial.Authority));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_RejectsAnEmptyValue(string? value)
    {
        Assert.False(RouteOrigin.TryParse(value, out _, out var error));
        Assert.Equal("The target origin is empty.", error);
    }

    [Theory]
    [InlineData("/relative/path")]
    [InlineData("example.com")]
    [InlineData("../escape")]
    public void TryParse_RejectsRelativeUris(string value)
    {
        Assert.False(RouteOrigin.TryParse(value, out _, out var error));
        Assert.Contains("is not an absolute URI", error);
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("file:///c:/windows")]
    [InlineData("ws://example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("example.com:8080")]
    [InlineData("//example.com")]
    public void TryParse_RejectsSchemesThatAreNotHttpOrHttps(string value)
    {
        Assert.False(RouteOrigin.TryParse(value, out _, out var error));
        Assert.Contains("is not supported. Use http or https.", error);
    }

    [Theory]
    [InlineData("http://example.com/api")]
    [InlineData("http://example.com/api/v1")]
    [InlineData("http://example.com/../escape")]
    public void TryParse_RejectsAnOriginCarryingAPath(string value)
    {
        Assert.False(RouteOrigin.TryParse(value, out _, out var error));
        Assert.Contains("carries a path", error);
    }

    [Theory]
    [InlineData("http://example.com/%2e%2e")]
    [InlineData("http://example.com/a/..")]
    public void TryParse_AcceptsAPathThatNormalisesAwayAndKeepsOnlyTheOrigin(string value)
    {
        // Uri collapses dot segments before the path is inspected, so these arrive as a
        // bare root. What is stored is still the authority alone.
        Assert.True(RouteOrigin.TryParse(value, out var origin, out _));
        Assert.Equal("http://example.com", origin!.GetLeftPart(UriPartial.Authority));
        Assert.Equal("/", origin.AbsolutePath);
    }

    [Theory]
    [InlineData("http://example.com/?q=1")]
    [InlineData("http://example.com?q=1")]
    [InlineData("http://example.com/#fragment")]
    [InlineData("http://example.com#fragment")]
    public void TryParse_RejectsAnOriginCarryingAQueryOrFragment(string value)
    {
        Assert.False(RouteOrigin.TryParse(value, out _, out var error));
        Assert.Contains("carries a query or fragment", error);
    }

    [Theory]
    [InlineData("http://user:password@example.com")]
    [InlineData("http://user@example.com")]
    public void TryParse_RefusesEmbeddedCredentials(string value)
    {
        Assert.False(RouteOrigin.TryParse(value, out _, out var error));
        Assert.Contains("must not embed credentials", error);
    }
}
