using Microsoft.Extensions.Configuration;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Tests.Configuration;

/// <summary>Which listen URLs the startup banner reports, resolved the way Kestrel resolves them.</summary>
public sealed class ModeBannerTests
{
    private const string KestrelDefault = "http://localhost:5000";

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value =>
                new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();

    [Fact]
    public void ResolveListenUrls_ReportsTheDeployedHttpsEndpoint()
    {
        var urls = ModeBanner.ResolveListenUrls(Config(
            ("Kestrel:Endpoints:Https:Url", "https://0.0.0.0:8091"),
            ("Kestrel:Endpoints:Https:Certificate:Path", "/certs/devconduit.pfx"),
            ("Kestrel:Endpoints:Https:Certificate:Password", "pfx-password")));

        Assert.Equal(["https://0.0.0.0:8091"], urls);
    }

    [Fact]
    public void ResolveListenUrls_ReportsASingleHttpEndpoint()
    {
        var urls = ModeBanner.ResolveListenUrls(Config(
            ("Kestrel:Endpoints:Http:Url", "http://localhost:8091")));

        Assert.Equal(["http://localhost:8091"], urls);
    }

    [Fact]
    public void ResolveListenUrls_ReportsEveryEndpointAndNothingElse()
    {
        var urls = ModeBanner.ResolveListenUrls(Config(
            ("Kestrel:Endpoints:Http:Url", "http://localhost:8090"),
            ("Kestrel:Endpoints:Https:Url", "https://localhost:8091")));

        Assert.Equal(["http://localhost:8090", "https://localhost:8091"], urls);
    }

    [Fact]
    public void ResolveListenUrls_TrimsAnEndpointUrl()
    {
        var urls = ModeBanner.ResolveListenUrls(Config(
            ("Kestrel:Endpoints:Http:Url", "  http://localhost:8091\t ")));

        Assert.Equal(["http://localhost:8091"], urls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveListenUrls_SkipsAnEndpointWithoutAUrl(string url)
    {
        var urls = ModeBanner.ResolveListenUrls(Config(
            ("Kestrel:Endpoints:Blank:Url", url),
            ("Kestrel:Endpoints:Http:Url", "http://localhost:8091")));

        Assert.Equal(["http://localhost:8091"], urls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveListenUrls_FallsBackToUrlsWhenTheOnlyEndpointHasNoUrl(string url)
    {
        var urls = ModeBanner.ResolveListenUrls(Config(
            ("Kestrel:Endpoints:Http:Url", url),
            ("urls", "http://localhost:7000")));

        Assert.Equal(["http://localhost:7000"], urls);
    }

    [Fact]
    public void ResolveListenUrls_FallsBackToTheDefaultWhenNoEndpointHasAUrlAndUrlsIsUnset()
    {
        var urls = ModeBanner.ResolveListenUrls(Config(
            ("Kestrel:Endpoints:Http:Url", ""),
            ("Kestrel:Endpoints:Https:Url", "   "),
            ("Kestrel:Endpoints:Https:Certificate:Path", "/certs/devconduit.pfx")));

        Assert.Equal([KestrelDefault], urls);
    }

    [Fact]
    public void ResolveListenUrls_SplitsAndTrimsTheUrlsKeyDroppingEmptyEntries()
    {
        var urls = ModeBanner.ResolveListenUrls(Config(
            ("urls", " http://a:1 ; ;https://b:2 ")));

        Assert.Equal(["http://a:1", "https://b:2"], urls);
    }

    [Fact]
    public void ResolveListenUrls_IgnoresUrlsWhenAnEndpointHasAUrl()
    {
        var urls = ModeBanner.ResolveListenUrls(Config(
            ("Kestrel:Endpoints:Https:Url", "https://0.0.0.0:8091"),
            ("urls", "http://localhost:7000;http://localhost:7001")));

        Assert.Equal(["https://0.0.0.0:8091"], urls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ; ;  ")]
    public void ResolveListenUrls_FallsBackToTheDefaultWhenUrlsHasNoEntries(string value)
    {
        var urls = ModeBanner.ResolveListenUrls(Config(("urls", value)));

        Assert.Equal([KestrelDefault], urls);
    }

    [Fact]
    public void ResolveListenUrls_ReportsKestrelsDefaultForAnEmptyConfiguration()
    {
        var urls = ModeBanner.ResolveListenUrls(Config());

        Assert.Equal([KestrelDefault], urls);
    }

    [Fact]
    public void ResolveConfiguredListenUrls_IsEmptyForAnEmptyConfiguration() =>
        Assert.Empty(ModeBanner.ResolveConfiguredListenUrls(Config()));

    [Fact]
    public void ResolveConfiguredListenUrls_IsEmptyWhenNoEndpointHasAUrlAndUrlsHasNoEntries()
    {
        var urls = ModeBanner.ResolveConfiguredListenUrls(Config(
            ("Kestrel:Endpoints:Http:Url", ""),
            ("Kestrel:Endpoints:Https:Url", "   "),
            ("Kestrel:Endpoints:Https:Certificate:Path", "/certs/devconduit.pfx"),
            ("urls", " ; ;  ")));

        Assert.Empty(urls);
    }

    [Theory]
    [InlineData("https://0.0.0.0:8091", null)]
    [InlineData("https://0.0.0.0:8091", "http://localhost:7000")]
    [InlineData(null, " http://a:1 ; ;https://b:2 ")]
    [InlineData("   ", "http://localhost:7000")]
    public void ResolveConfiguredListenUrls_MatchesResolveListenUrlsWhenSomethingIsConfigured(
        string? endpointUrl,
        string? urls)
    {
        var configuration = Config(("Kestrel:Endpoints:Main:Url", endpointUrl), ("urls", urls));

        var configured = ModeBanner.ResolveConfiguredListenUrls(configuration);

        Assert.NotEmpty(configured);
        Assert.Equal(ModeBanner.ResolveListenUrls(configuration), configured);
        Assert.DoesNotContain(KestrelDefault, configured);
    }
}
