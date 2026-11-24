using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>Where an import writes. <see cref="ForThisComputer"/> is the real machine; tests and probes pass their own.</summary>
public sealed record ProxyConfigImportTargets(
    HostsFile HostsFile,
    ClientCertificateDirectory ClientDirectory,
    string MachineSettingsPath,
    StoreName StoreName,
    StoreLocation StoreLocation,
    bool ProtectMachineSettings)
{
    public static ProxyConfigImportTargets ForThisComputer() =>
        new(
            HostsFile.ForThisComputer(),
            ClientCertificateDirectory.ForThisComputer(),
            SettingsFiles.ForThisComputer().MachinePath,
            StoreName.Root,
            StoreLocation.LocalMachine,
            ProtectMachineSettings: true);
}

/// <summary>
/// Which routes an import from one server added, so the next import from it can replace them,
/// and the address the file mapped the server to (absent from records written before it was kept).
/// </summary>
public sealed record ProxyConfigImportRecord(string Hostname, string Remote, IReadOnlyList<string> Routes, DateTimeOffset ImportedAt, string? Address = null);

/// <summary>
/// Applies a validated <c>.conduit</c> to this machine, idempotently: the hosts entry, the
/// server certificates in Windows trust and pinned in the app, the client certificate, and the
/// remote and its routes in the machine settings file. Everything that can be refused is
/// checked before the first change. Needs administrator rights on the real machine.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProxyConfigImporter
{
    private readonly ProxyConfigImportTargets _targets;
    private readonly TimeProvider _clock;

    public ProxyConfigImporter(ProxyConfigImportTargets targets, TimeProvider clock)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Imports the file and returns the lines of a summary for the person who ran it.</summary>
    /// <exception cref="InvalidOperationException">The import cannot go ahead; nothing was changed.</exception>
    public IReadOnlyList<string> Import(ValidatedProxyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var now = _clock.GetUtcNow();
        var summary = new List<string>
        {
            $"Server {config.Hostname}, port {config.Port}, remote '{config.Remote}'.",
            $"Client certificate {config.ClientCertificate.SerialNumber}, valid until {config.ClientCertificate.NotAfter.ToUniversalTime():yyyy-MM-dd}."
        };

        var records = ProxyConfigImportRecords.Read(_targets.ClientDirectory.ImportRecordPath);
        var previousRoutes = records.FirstOrDefault(record => string.Equals(record.Hostname, config.Hostname, StringComparison.OrdinalIgnoreCase))?.Routes ?? [];
        var settingsWriter = new SettingsFileWriter(
            _targets.MachineSettingsPath,
            _targets.ProtectMachineSettings ? MachineSettingsAccess.BuildSecurity : null);
        var settings = ProxyConfigSettingsMerge.Compute(settingsWriter.Read(), config, previousRoutes);

        var unmanaged = ManagedSettingKeys.Unmanaged(settings.Changes, ManagedSettingKeys.Imported);
        if (unmanaged.Count > 0)
        {
            throw new InvalidOperationException($"The import would change settings it does not manage: {string.Join(", ", unmanaged)}.");
        }

        if (config.AddressIsIp)
        {
            try
            {
                HostsFileText.ManagedLine(config.Address, config.Hostname);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException(exception.Message, exception);
            }
        }

        // App trust and the client identity first: the relay works from these alone.
        var folder = _targets.ClientDirectory.Folder(config.Hostname);
        folder.Create();
        var pins = PinnedTrust.Merge(folder.ReadTrust(), config.ServerCertificates.Select(PinnedTrust.Pin), now);
        folder.WriteTrust(pins);
        folder.WriteIdentity(config.ClientCertificatePkcs12);
        summary.Add($"Pinned {pins.Count} server certificate{(pins.Count == 1 ? string.Empty : "s")} in {folder.TrustPath}; client certificate stored in {folder.IdentityPath}.");

        summary.Add(SetHostsEntry(config));

        var trust = new RootStoreTrust(_targets.ClientDirectory.RootStoreRecordPath, _targets.StoreName, _targets.StoreLocation);
        var change = trust.Synchronize(config.Hostname, PinnedTrust.Load(pins), now);
        summary.Add(
            $"{trust.StoreDescription}: {change.Added.Count} added, {change.AlreadyPresent.Count} already present, {change.Removed.Count} expired or replaced removed.");

        settingsWriter.Apply(settings.Changes);
        summary.Add(
            $"Machine settings {_targets.MachineSettingsPath}: remote '{config.Remote}' -> {config.Endpoint}; "
            + (settings.ImportedRoutes.Count == 0 ? "no routes." : $"routes {string.Join(", ", settings.ImportedRoutes)}."));
        summary.AddRange(settings.Notes);

        records.RemoveAll(record => string.Equals(record.Hostname, config.Hostname, StringComparison.OrdinalIgnoreCase));
        records.Add(new ProxyConfigImportRecord(config.Hostname, config.Remote, settings.ImportedRoutes, now, config.Address));
        ProxyConfigImportRecords.Write(_targets.ClientDirectory.ImportRecordPath, records);

        summary.Add("Restart the local proxy (or the tray's local proxy) so it reads the new remote.");
        return summary;
    }

    private string SetHostsEntry(ValidatedProxyConfig config)
    {
        if (!config.AddressIsIp)
        {
            return $"Hosts file not changed: the address {config.Address} is a name, not an IP address, so {config.Hostname} must resolve through DNS.";
        }

        var edit = _targets.HostsFile.SetEntry(config.Address, config.Hostname);
        var line = edit.Changed
            ? $"Hosts file {_targets.HostsFile.Path}: {config.Hostname} -> {config.Address}."
            : $"Hosts file {_targets.HostsFile.Path}: {config.Hostname} -> {config.Address} was already there.";

        return edit.ShadowingLines.Count == 0
            ? line
            : line + $" Warning: an earlier line not managed by Dev.Conduit also maps {config.Hostname} and wins: {string.Join(" | ", edit.ShadowingLines)}";
    }
}
