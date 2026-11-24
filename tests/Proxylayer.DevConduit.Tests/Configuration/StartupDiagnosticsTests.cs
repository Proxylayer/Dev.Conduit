using Microsoft.Extensions.Configuration;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Tests.Configuration;

/// <summary>The redirect URI advice in the unconfigured notice, derived from the listen URLs and callback path.</summary>
public sealed class StartupDiagnosticsTests
{
    private const string HostPlaceholder = "<this server's host name>";

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new[]
                {
                    new KeyValuePair<string, string?>("AzureAd:TenantId", "<YOUR-TENANT-ID>"),
                    new KeyValuePair<string, string?>("AzureAd:ClientId", "<YOUR-CLIENT-ID>"),
                }
                .Concat(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value))))
            .Build();

    [Fact]
    public void UnconfiguredMessage_AdvisesTheHttpsEndpointWithAHostPlaceholderForAWildcardBind()
    {
        var message = StartupDiagnostics.UnconfiguredMessage(Config(
            ("Kestrel:Endpoints:Https:Url", "https://0.0.0.0:8091")));

        Assert.Contains($"  https://{HostPlaceholder}:8091/signin-oidc", message);
        Assert.Contains($"Replace {HostPlaceholder} with the name users browse to", message);
        Assert.DoesNotContain("0.0.0.0", message);
        Assert.DoesNotContain("http://localhost", message);
        Assert.Contains("register-app.ps1 -ServerBaseUrl <url>", message);
    }

    [Fact]
    public void UnconfiguredMessage_KeepsLocalhostForALocalHttpEndpoint()
    {
        var message = StartupDiagnostics.UnconfiguredMessage(Config(
            ("Kestrel:Endpoints:Http:Url", "http://localhost:8091")));

        Assert.Contains("  http://localhost:8091/signin-oidc", message);
        Assert.DoesNotContain(HostPlaceholder, message);
    }

    [Fact]
    public void UnconfiguredMessage_FallsBackToTheUrlsKey()
    {
        var message = StartupDiagnostics.UnconfiguredMessage(Config(
            ("urls", "http://*:5000;https://[::]:5001")));

        Assert.Contains($"  http://{HostPlaceholder}:5000/signin-oidc", message);
        Assert.Contains($"  https://{HostPlaceholder}:5001/signin-oidc", message);
        Assert.Contains("redirect URI for each address users browse to", message);
    }

    [Fact]
    public void UnconfiguredMessage_FallsBackToKestrelsDefaultWithNoListenUrls()
    {
        var message = StartupDiagnostics.UnconfiguredMessage(Config());

        Assert.Contains("  http://localhost:5000/signin-oidc", message);
    }

    [Fact]
    public void UnconfiguredMessage_UsesTheConfiguredCallbackPath()
    {
        var message = StartupDiagnostics.UnconfiguredMessage(Config(
            ("Kestrel:Endpoints:Https:Url", "https://0.0.0.0:8091"),
            ("AzureAd:CallbackPath", "/auth/callback")));

        Assert.Contains($"  https://{HostPlaceholder}:8091/auth/callback", message);
        Assert.DoesNotContain("8091/signin-oidc", message);
        Assert.Contains("It registers /signin-oidc, so change that redirect URI to end in /auth/callback", message);
    }

    [Fact]
    public void UnconfiguredMessage_StillReportsThePlaceholderIds()
    {
        var message = StartupDiagnostics.UnconfiguredMessage(Config());

        Assert.Contains("AzureAd:TenantId  (currently: <YOUR-TENANT-ID>)", message);
        Assert.Contains("AzureAd:ClientId  (currently: <YOUR-CLIENT-ID>)", message);
    }

    [Theory]
    [InlineData("http://0.0.0.0:8091")]
    [InlineData("http://[::]:8091")]
    [InlineData("http://*:8091")]
    [InlineData("http://+:8091")]
    [InlineData("http://devconduit.corp:8091")]
    [InlineData("http://10.0.0.5:8091")]
    [InlineData("http://[2001:db8::1]:8091")]
    [InlineData("HTTP://0.0.0.0:8091")]
    public void UnencryptedNetworkListenUrls_FlagsPlainHttpBeyondLoopback(string url)
    {
        var flagged = StartupDiagnostics.UnencryptedNetworkListenUrls(Config(("Kestrel:Endpoints:Http:Url", url)));

        Assert.Equal([url], flagged);
    }

    [Theory]
    [InlineData("http://localhost:8091")]
    [InlineData("http://LOCALHOST:8091")]
    [InlineData("http://127.0.0.1:8091")]
    [InlineData("http://127.0.0.2:8091")]
    [InlineData("http://[::1]:8091")]
    [InlineData("https://0.0.0.0:8091")]
    [InlineData("https://[::]:8091")]
    [InlineData("https://*:8091")]
    [InlineData("https://+:8091")]
    [InlineData("https://devconduit.corp:8091")]
    [InlineData("https://localhost:8091")]
    public void UnencryptedNetworkListenUrls_IgnoresLoopbackHttpAndEveryHttpsUrl(string url) =>
        Assert.Empty(StartupDiagnostics.UnencryptedNetworkListenUrls(Config(("Kestrel:Endpoints:Main:Url", url))));

    [Fact]
    public void UnencryptedNetworkListenUrls_ReportsOnlyTheOffendingEndpoints()
    {
        var flagged = StartupDiagnostics.UnencryptedNetworkListenUrls(Config(
            ("Kestrel:Endpoints:Local:Url", "http://localhost:8090"),
            ("Kestrel:Endpoints:Https:Url", "https://0.0.0.0:8091"),
            ("Kestrel:Endpoints:Wide:Url", "http://0.0.0.0:8092")));

        Assert.Equal(["http://0.0.0.0:8092"], flagged);
    }

    [Fact]
    public void UnencryptedNetworkListenUrls_FallsBackToTheUrlsKey()
    {
        var flagged = StartupDiagnostics.UnencryptedNetworkListenUrls(Config(
            ("urls", "http://*:5000;https://*:5001;http://localhost:5002;http://+:5003")));

        Assert.Equal(["http://*:5000", "http://+:5003"], flagged);
    }

    [Fact]
    public void UnencryptedNetworkListenUrls_IgnoresUrlsWhenAnEndpointIsConfigured() =>
        Assert.Empty(StartupDiagnostics.UnencryptedNetworkListenUrls(Config(
            ("Kestrel:Endpoints:Https:Url", "https://0.0.0.0:8091"),
            ("urls", "http://*:5000"))));

    [Fact]
    public void UnencryptedNetworkListenUrls_IsEmptyForKestrelsLoopbackDefault() =>
        Assert.Empty(StartupDiagnostics.UnencryptedNetworkListenUrls(Config()));
}
