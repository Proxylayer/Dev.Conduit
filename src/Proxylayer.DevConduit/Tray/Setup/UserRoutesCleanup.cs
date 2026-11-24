using System.Text.Json.Nodes;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Tray.Setup;

/// <summary>
/// After an imported server is removed, the routes in the user settings file that still point
/// at it: entries naming its remote, and entries that changed or switched off a route the
/// server's import added. Left in place they would name a remote that no longer exists, and
/// local mode would refuse to start. Pure.
/// </summary>
public static class UserRoutesCleanup
{
    /// <summary>The change that removes those entries, or null when the file has none.</summary>
    public static SettingsChangeSet? For(JsonObject userSettings, string remote, IReadOnlyCollection<string> removedRoutes)
    {
        ArgumentNullException.ThrowIfNull(userSettings);
        ArgumentNullException.ThrowIfNull(removedRoutes);

        if (SettingValues.Find(userSettings, ManagedSettingKeys.Routes) is not { } existing)
        {
            return null;
        }

        var routes = (JsonObject)existing.DeepClone();
        var removed = new HashSet<string>(removedRoutes, StringComparer.OrdinalIgnoreCase);
        var stale = routes
            .Where(route => removed.Contains(route.Key) || NamesRemote(route.Value, remote))
            .Select(route => route.Key)
            .ToList();

        if (stale.Count == 0)
        {
            return null;
        }

        foreach (var name in stale)
        {
            routes.Remove(name);
        }

        return routes.Count == 0
            ? new SettingsChangeSet().Remove(ManagedSettingKeys.Routes)
            : new SettingsChangeSet().Set(ManagedSettingKeys.Routes, routes);
    }

    private static bool NamesRemote(JsonNode? route, string remote) =>
        remote.Length > 0
        && route is JsonObject entry
        && entry.FirstOrDefault(property => string.Equals(property.Key, "Remote", StringComparison.OrdinalIgnoreCase)).Value is JsonValue value
        && value.TryGetValue<string>(out var name)
        && string.Equals(name.Trim(), remote, StringComparison.OrdinalIgnoreCase);
}
