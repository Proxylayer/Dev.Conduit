using Proxylayer.DevConduit.Models;
using Proxylayer.DevConduit.Services;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Services;

/// <summary>
/// Which headers count as secrets and what a history-safe copy looks like. The invariant
/// under test is that redaction happens on the way into history and nowhere else.
/// </summary>
public sealed class SecretHeaderPolicyTests
{
    private static SecretHeaderPolicy Policy(Action<Proxylayer.DevConduit.Configuration.DevConduitOptions>? configure = null) =>
        new(TestOptions.Create(configure));

    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("AUTHORIZATION")]
    [InlineData("Cookie")]
    [InlineData("Set-Cookie")]
    [InlineData("Proxy-Authorization")]
    [InlineData("X-Api-Key")]
    [InlineData("x-api-key")]
    [InlineData("api-key")]
    [InlineData("Ocp-Apim-Subscription-Key")]
    [InlineData("X-DevConduit-Upstream-Authorization")]
    [InlineData("x-devconduit-upstream-authorization")]
    [InlineData("X-DevConduit-Local-Secret")]
    [InlineData("x-devconduit-local-secret")]
    public void IsSecretName_MatchesTheDefaultListRegardlessOfCase(string name) =>
        Assert.True(Policy().IsSecretName(name));

    [Theory]
    [InlineData("Accept")]
    [InlineData("X-Correlation-Id")]
    [InlineData("Authorisation")]
    [InlineData("")]
    public void IsSecretName_LeavesOrdinaryHeadersAlone(string name) =>
        Assert.False(Policy().IsSecretName(name));

    [Fact]
    public void IsSecret_HonoursTheUserFlagOnAHeaderThatIsNotOnTheList()
    {
        var policy = Policy();

        Assert.True(policy.IsSecret(new HeaderItem("X-Tenant-Token", "abc", IsSecret: true)));
        Assert.False(policy.IsSecret(new HeaderItem("X-Tenant-Token", "abc")));
    }

    [Fact]
    public void IsSecret_TreatsAListedHeaderAsSecretEvenWithoutTheFlag() =>
        Assert.True(Policy().IsSecret(new HeaderItem("Authorization", "Bearer abc")));

    [Fact]
    public void IsSecretName_FollowsAReplacedConfiguredList()
    {
        var policy = Policy(options => options.SecretHeaderNames = ["X-Custom-Secret"]);

        Assert.True(policy.IsSecretName("x-custom-secret"));
        Assert.False(policy.IsSecretName("Authorization"));
    }

    [Fact]
    public void Redact_ReplacesASecretHeaderValueAndMarksIt()
    {
        var redacted = Policy().Redact(new HeaderItem("Authorization", "Bearer super-secret"));

        Assert.Equal(SecretHeaderPolicy.RedactedValue, redacted.Value);
        Assert.Equal("***redacted***", redacted.Value);
        Assert.True(redacted.IsSecret);
        Assert.Equal("Authorization", redacted.Name);
    }

    [Fact]
    public void Redact_LeavesAnOrdinaryHeaderUntouched()
    {
        var header = new HeaderItem("Accept", "application/json");

        Assert.Same(header, Policy().Redact(header));
    }

    [Fact]
    public void Redact_ReplacesTheUpstreamCredentialHeader()
    {
        var redacted = Policy().Redact(new HeaderItem(DevConduitHeaderNames.UpstreamAuthorization, "Bearer upstream"));

        Assert.Equal(SecretHeaderPolicy.RedactedValue, redacted.Value);
    }

    [Fact]
    public void Redact_ReplacesTheLocalSharedSecretHeader()
    {
        var redacted = Policy().Redact(new HeaderItem(DevConduitHeaderNames.LocalSecret, "the-shared-secret"));

        Assert.Equal(SecretHeaderPolicy.RedactedValue, redacted.Value);
        Assert.True(redacted.IsSecret);
    }

    [Fact]
    public void Redact_ReplacesSecretResponseHeaders()
    {
        var policy = Policy();

        Assert.Equal(SecretHeaderPolicy.RedactedValue, policy.Redact(new ResponseHeader("Set-Cookie", "s=1")).Value);
        Assert.Equal("text/plain", policy.Redact(new ResponseHeader("Content-Type", "text/plain")).Value);
    }

    [Fact]
    public void Redact_OnAResponseHeaderIgnoresTheUserFlagBecauseThereIsNone()
    {
        // ResponseHeader carries no per-header flag, so only the configured list applies.
        var policy = Policy(options => options.SecretHeaderNames = []);

        Assert.Equal("s=1", policy.Redact(new ResponseHeader("Set-Cookie", "s=1")).Value);
    }

    [Fact]
    public void Redact_OnARequestRedactsSecretsAndKeepsTheRest()
    {
        var request = new ProxyRequest(
            "POST",
            "https://example.com/api",
            [
                new HeaderItem("Authorization", "Bearer devconduit"),
                new HeaderItem(DevConduitHeaderNames.UpstreamAuthorization, "Bearer upstream"),
                new HeaderItem("X-Tenant-Token", "flagged", IsSecret: true),
                new HeaderItem("Accept", "application/json")
            ],
            Body: "{}");

        var redacted = Policy().Redact(request);

        Assert.Equal(SecretHeaderPolicy.RedactedValue, redacted.Headers[0].Value);
        Assert.Equal(SecretHeaderPolicy.RedactedValue, redacted.Headers[1].Value);
        Assert.Equal(SecretHeaderPolicy.RedactedValue, redacted.Headers[2].Value);
        Assert.Equal("application/json", redacted.Headers[3].Value);

        // The body and the rest of the request travel through untouched.
        Assert.Equal("{}", redacted.Body);
        Assert.Equal("https://example.com/api", redacted.Url);
    }

    [Fact]
    public void Redact_DoesNotMutateTheRequestItWasGiven()
    {
        var request = new ProxyRequest(
            "GET",
            "https://example.com/",
            [new HeaderItem("Authorization", "Bearer live-token")]);

        Policy().Redact(request);

        Assert.Equal("Bearer live-token", request.Headers[0].Value);
    }
}
