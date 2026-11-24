using System.Text.Json;

namespace Proxylayer.DevConduit.Updates;

/// <summary>What a feed response says: the release found, or why none counts.</summary>
/// <param name="Release">The newest release that counts, with both assets; null when none does.</param>
/// <param name="Reason">Why no release counts, for the log; empty when one does.</param>
public sealed record ReleaseParse(UpdateRelease? Release, string Reason);

/// <summary>
/// Reads GitHub's release JSON. A release counts when it is published (not a draft), is not a
/// prerelease unless prereleases are included, has a <c>vX.Y.Z</c> tag, and carries both
/// <c>Dev.Conduit-X.Y.Z.msi</c> and <c>Dev.Conduit-X.Y.Z.msi.sha256</c> with https download addresses.
/// Pure: no network, no clock.
/// </summary>
public static class GitHubReleaseParser
{
    /// <summary>One release object, as <c>releases/latest</c> and <c>releases/tags/{tag}</c> return.</summary>
    public static ReleaseParse ParseRelease(string json, bool includePrerelease)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? FromElement(document.RootElement, includePrerelease)
                : new ReleaseParse(null, "The feed did not answer with a release.");
        }
        catch (JsonException exception)
        {
            return new ReleaseParse(null, $"The feed's answer is not valid JSON: {exception.Message}");
        }
    }

    /// <summary>A list of releases, as <c>releases</c> returns: the newest that counts.</summary>
    public static ReleaseParse ParseList(string json, bool includePrerelease)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new ReleaseParse(null, "The feed did not answer with a list of releases.");
            }

            UpdateRelease? newest = null;
            var reasons = new List<string>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var parsed = FromElement(element, includePrerelease);
                if (parsed.Release is { } release)
                {
                    if (newest is null || release.Version > newest.Version)
                    {
                        newest = release;
                    }
                }
                else
                {
                    reasons.Add(parsed.Reason);
                }
            }

            return newest is not null
                ? new ReleaseParse(newest, string.Empty)
                : new ReleaseParse(null, reasons.Count == 0 ? "The feed lists no releases." : reasons[0]);
        }
        catch (JsonException exception)
        {
            return new ReleaseParse(null, $"The feed's answer is not valid JSON: {exception.Message}");
        }
    }

    private static ReleaseParse FromElement(JsonElement release, bool includePrerelease)
    {
        if (release.ValueKind != JsonValueKind.Object)
        {
            return new ReleaseParse(null, "The feed listed something that is not a release.");
        }

        var tag = String(release, "tag_name");
        if (Bool(release, "draft"))
        {
            return new ReleaseParse(null, $"Release {tag} is a draft.");
        }

        if (!includePrerelease && (Bool(release, "prerelease") || ReleaseVersion.IsPrereleaseLabel(tag)))
        {
            return new ReleaseParse(null, $"Release {tag} is a prerelease.");
        }

        if (tag is null || !tag.StartsWith('v') || !ReleaseVersion.TryParse(tag, out var version))
        {
            return new ReleaseParse(null, $"Release tag '{tag}' is not vX.Y.Z.");
        }

        var msiName = ReleaseAssetNames.Msi(version);
        var checksumName = ReleaseAssetNames.Checksum(version);
        Uri? msiUrl = null;
        Uri? checksumUrl = null;
        long msiSize = 0;

        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = String(asset, "name");
                var url = DownloadUrl(asset);
                if (url is null)
                {
                    continue;
                }

                if (string.Equals(name, msiName, StringComparison.Ordinal))
                {
                    msiUrl = url;
                    msiSize = asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0;
                }
                else if (string.Equals(name, checksumName, StringComparison.Ordinal))
                {
                    checksumUrl = url;
                }
            }
        }

        if (msiUrl is null || checksumUrl is null)
        {
            return new ReleaseParse(null, $"Release {tag} does not carry both {msiName} and {checksumName}.");
        }

        return new ReleaseParse(new UpdateRelease(version, tag, msiUrl, msiSize, checksumUrl), string.Empty);
    }

    private static Uri? DownloadUrl(JsonElement asset) =>
        Uri.TryCreate(String(asset, "browser_download_url"), UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
            ? url
            : null;

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
