using System.Text.Json.Nodes;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>How removing an imported server changes the machine settings file.</summary>
/// <param name="Changes">Whole <c>Remotes</c> and <c>Routes</c> sections without the server's entries; empty when nothing names it.</param>
/// <param name="Remote">The remote removed, or null when no remote points at the server.</param>
/// <param name="RemovedRoutes">The routes removed with it.</param>
public sealed record ImportedServerSettings(SettingsChangeSet Changes, string? Remote, IReadOnlyList<string> RemovedRoutes);

/// <summary>
/// Works out how removing an imported server changes the machine settings file, the reverse
/// of <see cref="ProxyConfigSettingsMerge"/>: the remote whose endpoint names the server goes,
/// with every route naming that remote and every route the server's import added without
/// one. The AzureAd ids an import filled in stay, since other servers may share them. Pure.
/// </summary>
public static class ImportedServerSettingsRemoval
{
    public static ImportedServerSettings Compute(JsonObject machineSettings, string hostname, ProxyConfigImportRecord? record)
    {
        ArgumentNullException.ThrowIfNull(machineSettings);
        hostname = DnsHostname.Normalize(hostname);

        var devConduit = ProxyConfigSettingsMerge.Section(machineSettings, "DevConduit");
        var remotes = (ProxyConfigSettingsMerge.Section(devConduit, "Remotes")?.DeepClone() as JsonObject) ?? new JsonObject(SettingsJson.NodeOptions);
        var routes = (ProxyConfigSettingsMerge.Section(devConduit, "Routes")?.DeepClone() as JsonObject) ?? new JsonObject(SettingsJson.NodeOptions);

        // The recorded name first, as long as it still points at this server; else any remote that does.
        var remote = record is not null
                     && ProxyConfigSettingsMerge.Find(remotes, record.Remote) is { } recorded
                     && remotes[recorded] is { } recordedNode
                     && ProxyConfigSettingsMerge.PointsAt(recordedNode, hostname)
            ? recorded
            : remotes.Where(entry => entry.Value is not null && ProxyConfigSettingsMerge.PointsAt(entry.Value, hostname))
                .Select(entry => entry.Key)
                .FirstOrDefault();

        var imported = new HashSet<string>(record?.Routes ?? [], StringComparer.OrdinalIgnoreCase);
        var removedRoutes = new List<string>();
        foreach (var name in routes.Select(route => route.Key).ToList())
        {
            var owner = ProxyConfigSettingsMerge.RemoteOf(routes[name]);
            var ownedByRemote = remote is not null && string.Equals(owner, remote, StringComparison.OrdinalIgnoreCase);
            if (ownedByRemote || (imported.Contains(name) && string.IsNullOrEmpty(owner)))
            {
                routes.Remove(name);
                removedRoutes.Add(name);
            }
        }

        var changes = new SettingsChangeSet();
        if (remote is not null)
        {
            remotes.Remove(remote);
            SetOrRemove(changes, ManagedSettingKeys.Remotes, remotes);
        }

        if (removedRoutes.Count > 0)
        {
            SetOrRemove(changes, ManagedSettingKeys.Routes, routes);
        }

        return new ImportedServerSettings(changes, remote, removedRoutes);
    }

    private static void SetOrRemove(SettingsChangeSet changes, string key, JsonObject section)
    {
        if (section.Count == 0)
        {
            changes.Remove(key);
        }
        else
        {
            changes.Set(key, section);
        }
    }
}
