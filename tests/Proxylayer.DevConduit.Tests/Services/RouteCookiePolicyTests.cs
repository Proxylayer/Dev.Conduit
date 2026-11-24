using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Tests.Services;

/// <summary>
/// What survives of a caller's Cookie header. This guards a fixed defect: Dev.Conduit's own
/// session cookie must never reach an upstream, whatever the forwarding setting says.
/// </summary>
public sealed class RouteCookiePolicyTests
{
    private const string Session = "DevConduit.Auth=chunk-of-session";
    private const string Antiforgery = ".AspNetCore.Antiforgery.Ab1cD=token";
    private const string Correlation = ".AspNetCore.Correlation.OpenIdConnect.xyz=state";
    private const string Nonce = ".AspNetCore.OpenIdConnect.Nonce.abc=nonce";

    [Theory]
    [InlineData("app=1; other=2")]
    [InlineData(Session)]
    [InlineData(Antiforgery)]
    [InlineData("app=1; " + Session)]
    public void Filter_SendsNothingWhenForwardingIsOff(string cookieHeader) =>
        Assert.Null(RouteCookiePolicy.Filter(cookieHeader, forwardOthers: false));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Filter_SendsNothingForAnAbsentHeader(string? cookieHeader)
    {
        Assert.Null(RouteCookiePolicy.Filter(cookieHeader, forwardOthers: true));
        Assert.Null(RouteCookiePolicy.Filter(cookieHeader, forwardOthers: false));
    }

    [Fact]
    public void Filter_ForwardsOrdinaryCookiesWhenAsked() =>
        Assert.Equal("app=1; other=2", RouteCookiePolicy.Filter("app=1; other=2", forwardOthers: true));

    [Theory]
    [InlineData(Session)]
    [InlineData(Antiforgery)]
    [InlineData(Correlation)]
    [InlineData(Nonce)]
    public void Filter_StripsDevConduitOwnedCookiesEvenWhenForwardingIsOn(string owned)
    {
        var result = RouteCookiePolicy.Filter($"app=1; {owned}; other=2", forwardOthers: true);

        Assert.Equal("app=1; other=2", result);
        Assert.DoesNotContain("DevConduit.Auth", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".AspNetCore.", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Filter_SendsNothingWhenOnlyDevConduitCookiesWereOffered() =>
        Assert.Null(RouteCookiePolicy.Filter($"{Session}; {Antiforgery}; {Correlation}; {Nonce}", forwardOthers: true));

    [Theory]
    [InlineData("devconduit.auth=x")]
    [InlineData("DEVCONDUIT.AUTH=x")]
    [InlineData(".aspnetcore.antiforgery.zz=x")]
    [InlineData(".ASPNETCORE.CORRELATION.zz=x")]
    [InlineData(".AspNetCore.OpenIdConnect.NONCE.zz=x")]
    public void Filter_MatchesOwnedCookieNamesWithoutRegardToCase(string owned) =>
        Assert.Equal("app=1", RouteCookiePolicy.Filter($"{owned}; app=1", forwardOthers: true));

    [Fact]
    public void Filter_StripsAnOwnedCookieWrittenWithoutAValue() =>
        Assert.Equal("app=1", RouteCookiePolicy.Filter("DevConduit.Auth; app=1", forwardOthers: true));

    [Fact]
    public void Filter_ToleratesWhitespaceAndEmptyPairs() =>
        Assert.Equal("app=1; other=2", RouteCookiePolicy.Filter($"  app=1 ;; {Session} ;  other=2  ;", forwardOthers: true));

    [Fact]
    public void Filter_KeepsACookieWhoseValueMentionsAnOwnedName() =>
        Assert.Equal(
            "app=DevConduit.Auth",
            RouteCookiePolicy.Filter($"app=DevConduit.Auth; {Session}", forwardOthers: true));

    [Fact]
    public void Filter_KeepsACookieWhoseNameMerelyContainsAnOwnedName()
    {
        // The rule is a prefix on the name, not a substring anywhere in the pair.
        Assert.Equal(
            "not.DevConduit.Auth=1",
            RouteCookiePolicy.Filter("not.DevConduit.Auth=1", forwardOthers: true));
    }

    [Fact]
    public void Filter_StripsEveryDevConduitCookieWhenSeveralAreSentTogether()
    {
        var header = string.Join("; ", Session, "a=1", Antiforgery, "b=2", Correlation, "c=3", Nonce);

        Assert.Equal("a=1; b=2; c=3", RouteCookiePolicy.Filter(header, forwardOthers: true));
    }

    [Fact]
    public void SessionCookieName_IsTheConsoleSignInCookie() =>
        Assert.Equal("DevConduit.Auth", RouteCookiePolicy.SessionCookieName);
}
