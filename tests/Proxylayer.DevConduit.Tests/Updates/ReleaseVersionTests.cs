using Proxylayer.DevConduit.Updates;

namespace Proxylayer.DevConduit.Tests.Updates;

/// <summary>Release tags, informational versions and MSI product versions, compared on major.minor.patch.</summary>
public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.0.1.0", "1.0.1")]
    [InlineData("1.2.3+abcdef0", "1.2.3")]
    [InlineData("1.2.3-beta.1", "1.2.3")]
    [InlineData(" 10.20.30 ", "10.20.30")]
    public void TryParse_ReadsMajorMinorPatch(string text, string expected)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.x.3")]
    [InlineData("1..3")]
    [InlineData("-1.2.3")]
    [InlineData("1.2.3 4")]
    [InlineData("latest")]
    public void TryParse_RefusesAnythingElse(string? text) =>
        Assert.False(ReleaseVersion.TryParse(text, out _));

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.1.0", "1.0.9", true)]
    [InlineData("2.0.0", "1.99.99", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("1.0.1.0", "1.0.1", false)]
    public void IsNewer_ComparesMajorMinorPatchOnly(string candidate, string current, bool newer) =>
        Assert.Equal(newer, ReleaseVersion.IsNewer(Version.Parse(candidate), Version.Parse(current)));

    [Fact]
    public void Normalize_DropsTheFourthPartAndFillsAMissingPatch()
    {
        Assert.Equal(new Version(1, 2, 3), ReleaseVersion.Normalize(new Version(1, 2, 3, 4)));
        Assert.Equal(new Version(1, 2, 0), ReleaseVersion.Normalize(new Version(1, 2)));
    }

    [Theory]
    [InlineData("v1.2.0-beta.1", true)]
    [InlineData("v1.2.0", false)]
    [InlineData("1.2.0+build-7", false)]
    public void IsPrereleaseLabel_LooksAtTheLabelBeforeAnyMetadata(string text, bool prerelease) =>
        Assert.Equal(prerelease, ReleaseVersion.IsPrereleaseLabel(text));

    [Fact]
    public void Current_IsThisBuildsVersionWithoutMetadata()
    {
        Assert.DoesNotContain('+', ReleaseVersion.CurrentDisplay);
        Assert.True(ReleaseVersion.TryParse(ReleaseVersion.CurrentDisplay, out var display));
        Assert.Equal(ReleaseVersion.Current, display);
    }

    [Fact]
    public void AssetNames_AreTheOnesTheReleaseWorkflowProduces()
    {
        Assert.Equal("Dev.Conduit-1.2.3.msi", ReleaseAssetNames.Msi(new Version(1, 2, 3)));
        Assert.Equal("Dev.Conduit-1.2.3.msi.sha256", ReleaseAssetNames.Checksum(new Version(1, 2, 3, 0)));
    }

    [Theory]
    [InlineData("https://github.com/Proxylayer/Dev.Conduit", "Proxylayer", "Dev.Conduit")]
    [InlineData("https://github.com/Proxylayer/Dev.Conduit/", "Proxylayer", "Dev.Conduit")]
    [InlineData("https://github.com/Proxylayer/Dev.Conduit.git", "Proxylayer", "Dev.Conduit")]
    [InlineData("https://GitHub.com/owner/repo", "owner", "repo")]
    public void GitHubRepository_ParsesARepositoryAddress(string feed, string owner, string name)
    {
        Assert.True(GitHubRepository.TryParse(feed, out var repository));
        Assert.Equal(owner, repository.Owner);
        Assert.Equal(name, repository.Name);
        Assert.Equal($"https://api.github.com/repos/{owner}/{name}/releases/latest", repository.LatestReleaseUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://github.com/Proxylayer/Dev.Conduit")]
    [InlineData("https://example.com/Proxylayer/Dev.Conduit")]
    [InlineData("https://github.com/Proxylayer")]
    [InlineData("https://github.com/Proxylayer/Dev.Conduit/releases")]
    [InlineData("https://github.com/../x")]
    [InlineData("https://github.com/Proxylayer/Dev.Conduit?x=1")]
    [InlineData("not a url")]
    public void GitHubRepository_RefusesAnythingElse(string feed) =>
        Assert.False(GitHubRepository.TryParse(feed, out _));
}
