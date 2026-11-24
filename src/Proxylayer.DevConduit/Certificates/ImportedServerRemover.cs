using System.Runtime.Versioning;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>What removing an imported server did.</summary>
/// <param name="Found">False when nothing on this machine named the server; nothing was changed then.</param>
/// <param name="Remote">The remote removed from the machine settings file, or null.</param>
/// <param name="RemovedRoutes">The routes removed with it.</param>
/// <param name="Summary">Lines for the person who ran it.</param>
public sealed record ImportedServerRemoval(bool Found, string? Remote, IReadOnlyList<string> RemovedRoutes, IReadOnlyList<string> Summary);

/// <summary>
/// Undoes what <see cref="ProxyConfigImporter"/> did for one server: its remote and routes in
/// the machine settings file, its machine-layer client folder (pinned certificates and client
/// identity; files deleted one by one, no link followed), its hosts line, the certificates this
/// app added to Windows trust for it, and its import record. Nothing this app did not write is
/// touched, and no user's own layer: the tray removes the current user's afterwards
/// (<see cref="ClientTrustLayers.ForgetUser"/>). Needs administrator rights on the real machine.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ImportedServerRemover
{
    private readonly ProxyConfigImportTargets _targets;
    private readonly TimeProvider _clock;

    public ImportedServerRemover(ProxyConfigImportTargets targets, TimeProvider clock)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <exception cref="ArgumentException"><paramref name="hostname"/> is not a DNS host name.</exception>
    /// <exception cref="InvalidOperationException">The settings change would touch keys an import does not manage; nothing was changed.</exception>
    public ImportedServerRemoval Remove(string hostname)
    {
        hostname = DnsHostname.Normalize(hostname);
        if (!DnsHostname.IsValid(hostname))
        {
            throw new ArgumentException($"'{hostname}' is not a DNS host name.", nameof(hostname));
        }

        var records = ProxyConfigImportRecords.Read(_targets.ClientDirectory.ImportRecordPath);
        var record = records.FirstOrDefault(entry => string.Equals(entry.Hostname, hostname, StringComparison.OrdinalIgnoreCase));
        var settingsWriter = new SettingsFileWriter(
            _targets.MachineSettingsPath,
            _targets.ProtectMachineSettings ? MachineSettingsAccess.BuildSecurity : null);
        var settings = ImportedServerSettingsRemoval.Compute(settingsWriter.Read(), hostname, record);

        var unmanaged = ManagedSettingKeys.Unmanaged(settings.Changes, ManagedSettingKeys.Imported);
        if (unmanaged.Count > 0)
        {
            throw new InvalidOperationException($"Removing the server would change settings it does not manage: {string.Join(", ", unmanaged)}.");
        }

        var folder = _targets.ClientDirectory.Folder(hostname);
        var trust = new RootStoreTrust(_targets.ClientDirectory.RootStoreRecordPath, _targets.StoreName, _targets.StoreLocation);
        var added = trust.AddedFor(hostname);
        var hostsAddress = _targets.HostsFile.ManagedAddress(hostname);

        if (record is null && settings.Changes.IsEmpty && added.Count == 0 && hostsAddress is null && !Directory.Exists(folder.Directory))
        {
            return new ImportedServerRemoval(false, null, [], [$"Nothing on this machine was imported for {hostname}."]);
        }

        var summary = new List<string>();

        if (!settings.Changes.IsEmpty)
        {
            settingsWriter.Apply(settings.Changes);
            summary.Add(
                $"Machine settings {_targets.MachineSettingsPath}: "
                + (settings.Remote is null ? "no remote pointed at it" : $"remote '{settings.Remote}' removed")
                + (settings.RemovedRoutes.Count == 0 ? "; no routes." : $"; routes {string.Join(", ", settings.RemovedRoutes)} removed."));
        }

        if (added.Count > 0)
        {
            var change = trust.Synchronize(hostname, [], _clock.GetUtcNow());
            summary.Add($"{trust.StoreDescription}: {change.Removed.Count} certificate{(change.Removed.Count == 1 ? string.Empty : "s")} this app added removed.");
        }

        if (hostsAddress is not null)
        {
            _targets.HostsFile.RemoveEntry(hostname);
            summary.Add($"Hosts file {_targets.HostsFile.Path}: the line for {hostname} removed.");
        }

        summary.Add(folder.Delete());

        if (record is not null)
        {
            records.Remove(record);
            ProxyConfigImportRecords.Write(_targets.ClientDirectory.ImportRecordPath, records);
        }

        summary.Add("Restart the local proxy so it stops using the server.");
        return new ImportedServerRemoval(true, settings.Remote, settings.RemovedRoutes, summary);
    }
}
