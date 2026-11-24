using System.Text.Json.Nodes;

namespace Proxylayer.DevConduit.Tests.Updates;

/// <summary>GitHub release JSON as the API returns it, built for the feed tests.</summary>
internal static class ReleaseJson
{
    public const string Download = "https://github.com/Proxylayer/Dev.Conduit/releases/download";

    public static JsonObject Release(string tag, bool draft = false, bool prerelease = false, params string[] assets)
    {
        var list = new JsonArray();
        foreach (var name in assets)
        {
            list.Add(new JsonObject
            {
                ["name"] = name,
                ["size"] = 1234,
                ["browser_download_url"] = $"{Download}/{tag}/{name}"
            });
        }

        return new JsonObject
        {
            ["tag_name"] = tag,
            ["draft"] = draft,
            ["prerelease"] = prerelease,
            ["assets"] = list
        };
    }

    /// <summary>A published release carrying both assets the updater needs.</summary>
    public static JsonObject Complete(string version, bool draft = false, bool prerelease = false) =>
        Release($"v{version}", draft, prerelease, $"Dev.Conduit-{version}.msi", $"Dev.Conduit-{version}.msi.sha256", $"Dev.Conduit-{version}-installer.zip");

    public static string List(params JsonObject[] releases)
    {
        var array = new JsonArray();
        foreach (var release in releases)
        {
            array.Add(release);
        }

        return array.ToJsonString();
    }
}
