using Proxylayer.DevConduit.Updates;

namespace Proxylayer.DevConduit.Tests.Updates;

/// <summary>Which releases count: published, not prereleases unless asked, tagged vX.Y.Z, with both assets.</summary>
public sealed class GitHubReleaseParserTests
{
    [Fact]
    public void ParseRelease_FindsBothAssetsByTheirExactNames()
    {
        var parse = GitHubReleaseParser.ParseRelease(ReleaseJson.Complete("1.4.0").ToJsonString(), includePrerelease: false);

        var release = Assert.IsType<UpdateRelease>(parse.Release);
        Assert.Equal(new Version(1, 4, 0), release.Version);
        Assert.Equal("v1.4.0", release.Tag);
        Assert.Equal($"{ReleaseJson.Download}/v1.4.0/Dev.Conduit-1.4.0.msi", release.MsiUrl.AbsoluteUri);
        Assert.Equal($"{ReleaseJson.Download}/v1.4.0/Dev.Conduit-1.4.0.msi.sha256", release.ChecksumUrl.AbsoluteUri);
        Assert.Equal(1234, release.MsiSize);
    }

    [Fact]
    public void ParseRelease_RefusesADraft()
    {
        var parse = GitHubReleaseParser.ParseRelease(ReleaseJson.Complete("1.4.0", draft: true).ToJsonString(), includePrerelease: true);

        Assert.Null(parse.Release);
        Assert.Contains("draft", parse.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ParseRelease_TakesAPrereleaseOnlyWhenAsked(bool includePrerelease, bool found)
    {
        var parse = GitHubReleaseParser.ParseRelease(ReleaseJson.Complete("1.4.0", prerelease: true).ToJsonString(), includePrerelease);

        Assert.Equal(found, parse.Release is not null);
    }

    [Fact]
    public void ParseRelease_TreatsAPrereleaseLabelInTheTagAsAPrerelease()
    {
        var json = ReleaseJson.Release("v1.4.0-rc.1", assets: ["Dev.Conduit-1.4.0.msi", "Dev.Conduit-1.4.0.msi.sha256"]).ToJsonString();

        Assert.Null(GitHubReleaseParser.ParseRelease(json, includePrerelease: false).Release);
    }

    [Theory]
    [InlineData("Dev.Conduit-1.4.0.msi")]
    [InlineData("Dev.Conduit-1.4.0.msi.sha256")]
    public void ParseRelease_RefusesAReleaseMissingEitherAsset(string only)
    {
        var parse = GitHubReleaseParser.ParseRelease(ReleaseJson.Release("v1.4.0", assets: [only]).ToJsonString(), includePrerelease: false);

        Assert.Null(parse.Release);
        Assert.Contains("does not carry both", parse.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dev.conduit-1.4.0.msi", "Dev.Conduit-1.4.0.msi.sha256")]
    [InlineData("Dev.Conduit-1.3.0.msi", "Dev.Conduit-1.3.0.msi.sha256")]
    [InlineData("Dev.Conduit-1.4.0-installer.zip", "Dev.Conduit-1.4.0.msi.sha256")]
    public void ParseRelease_MatchesAssetNamesExactlyForTheTagsVersion(string msi, string checksum) =>
        Assert.Null(GitHubReleaseParser.ParseRelease(ReleaseJson.Release("v1.4.0", assets: [msi, checksum]).ToJsonString(), false).Release);

    [Theory]
    [InlineData("1.4.0")]
    [InlineData("release-1.4.0")]
    [InlineData("v1.4")]
    public void ParseRelease_RefusesATagThatIsNotVxyz(string tag) =>
        Assert.Null(GitHubReleaseParser.ParseRelease(
            ReleaseJson.Release(tag, assets: ["Dev.Conduit-1.4.0.msi", "Dev.Conduit-1.4.0.msi.sha256"]).ToJsonString(), false).Release);

    [Fact]
    public void ParseRelease_RefusesAnAssetThatIsNotHttps()
    {
        var json = ReleaseJson.Complete("1.4.0").ToJsonString().Replace("https://github.com", "http://github.com", StringComparison.Ordinal);

        Assert.Null(GitHubReleaseParser.ParseRelease(json, includePrerelease: false).Release);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    public void ParseRelease_RefusesWhatIsNotAReleaseObject(string json) =>
        Assert.Null(GitHubReleaseParser.ParseRelease(json, includePrerelease: false).Release);

    [Fact]
    public void ParseList_TakesTheNewestThatCountsSkippingDraftsAndPrereleases()
    {
        var json = ReleaseJson.List(
            ReleaseJson.Complete("2.0.0", draft: true),
            ReleaseJson.Complete("1.9.0", prerelease: true),
            ReleaseJson.Complete("1.7.0"),
            ReleaseJson.Complete("1.8.0"),
            ReleaseJson.Release("v1.8.5", assets: ["Dev.Conduit-1.8.5.msi"]));

        Assert.Equal(new Version(1, 8, 0), GitHubReleaseParser.ParseList(json, includePrerelease: false).Release?.Version);
        Assert.Equal(new Version(1, 9, 0), GitHubReleaseParser.ParseList(json, includePrerelease: true).Release?.Version);
    }

    [Fact]
    public void ParseList_WithNothingThatCountsSaysWhy()
    {
        var parse = GitHubReleaseParser.ParseList(ReleaseJson.List(ReleaseJson.Complete("2.0.0", draft: true)), includePrerelease: false);

        Assert.Null(parse.Release);
        Assert.NotEmpty(parse.Reason);
    }
}
