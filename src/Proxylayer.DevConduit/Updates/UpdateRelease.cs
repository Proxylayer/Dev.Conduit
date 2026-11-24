using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Proxylayer.DevConduit.Updates;

/// <summary>A published release with the two assets an update needs.</summary>
/// <param name="Version">Major.minor.patch, from the tag.</param>
/// <param name="Tag">The tag as GitHub reports it, for example <c>v1.2.0</c>.</param>
/// <param name="MsiUrl">The <c>Dev.Conduit-X.Y.Z.msi</c> asset's download address.</param>
/// <param name="MsiSize">The MSI's size in bytes as the feed reports it.</param>
/// <param name="ChecksumUrl">The <c>Dev.Conduit-X.Y.Z.msi.sha256</c> asset's download address.</param>
public sealed record UpdateRelease(Version Version, string Tag, Uri MsiUrl, long MsiSize, Uri ChecksumUrl)
{
    public string MsiName => ReleaseAssetNames.Msi(Version);

    public string ChecksumName => ReleaseAssetNames.Checksum(Version);
}

/// <summary>The asset names the release workflow produces, and the updater looks for, exactly.</summary>
public static class ReleaseAssetNames
{
    public static string Msi(Version version) => $"Dev.Conduit-{ReleaseVersion.Format(version)}.msi";

    public static string Checksum(Version version) => Msi(version) + ".sha256";
}

/// <summary>A GitHub repository named by a feed address such as <c>https://github.com/Proxylayer/Dev.Conduit</c>.</summary>
public sealed partial record GitHubRepository(string Owner, string Name)
{
    public const string ApiBase = "https://api.github.com";

    /// <summary>The <c>releases/latest</c> endpoint, which reports neither drafts nor prereleases.</summary>
    public Uri LatestReleaseUri => new($"{ApiBase}/repos/{Owner}/{Name}/releases/latest");

    /// <summary>The newest releases, prereleases included; drafts are not shown to an anonymous caller.</summary>
    public Uri ReleaseListUri => new($"{ApiBase}/repos/{Owner}/{Name}/releases?per_page=20");

    public Uri ReleaseByTagUri(Version version) => new($"{ApiBase}/repos/{Owner}/{Name}/releases/tags/v{ReleaseVersion.Format(version)}");

    /// <summary>
    /// The repository a feed address names: <c>https://github.com/owner/name</c>, with an optional
    /// trailing slash or <c>.git</c>. Anything else, including another host, is refused.
    /// </summary>
    public static bool TryParse(string? feedUrl, [NotNullWhen(true)] out GitHubRepository? repository)
    {
        repository = null;
        if (!Uri.TryCreate(feedUrl?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 2)
        {
            return false;
        }

        var name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        if (!Segment().IsMatch(segments[0]) || !Segment().IsMatch(name) || segments[0].Trim('.').Length == 0 || name.Trim('.').Length == 0)
        {
            return false;
        }

        repository = new GitHubRepository(segments[0], name);
        return true;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,100}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Segment();
}
