using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Tray.Setup;

/// <summary>Where one server's certificates stand on this machine, as read from its client folder and the Windows store.</summary>
/// <param name="PinnedCount">The server certificates pinned for the server that are still valid.</param>
/// <param name="TrustedUntil">The latest expiry among the pinned certificates Windows also trusts, or null when it trusts none.</param>
/// <param name="RootStoreUpdateNeeded">True when a valid pinned certificate is missing from <c>LocalMachine\Root</c>.</param>
/// <param name="MachineIdentityRenewalDue">True when the machine layer's client certificate is inside its renewal window.</param>
public sealed record ServerTrustFacts(int PinnedCount, DateTimeOffset? TrustedUntil, bool RootStoreUpdateNeeded, bool MachineIdentityRenewalDue = false)
{
    public static ServerTrustFacts None { get; } = new(0, null, false);

    /// <summary>
    /// True when <c>service-admin trust</c> has something to do: a valid pinned certificate to add
    /// to Windows trust, or the machine identity to renew.
    /// </summary>
    public bool UpdateNeeded => (RootStoreUpdateNeeded && PinnedCount > 0) || MachineIdentityRenewalDue;
}

/// <summary>One row of the Servers page: one imported server.</summary>
/// <param name="Name">The remote's name, or blank when no remote points at the server any more.</param>
/// <param name="Hostname">The server's host name.</param>
/// <param name="Address">What the hosts file (or else the import) maps the host name to.</param>
/// <param name="Certificate">The server certificate's status in plain words, from the client folder and the Windows store.</param>
/// <param name="LastSync">When the local proxy last synced with the server, in plain words.</param>
/// <param name="Routes">How many routes go to the server.</param>
/// <param name="TrustUpdateNeeded">True when <c>service-admin trust</c> would add a certificate to Windows trust or renew the machine identity.</param>
public sealed record ImportedServerRow(
    string Name,
    string Hostname,
    string Address,
    string Certificate,
    string LastSync,
    int Routes,
    bool TrustUpdateNeeded);

/// <summary>
/// The Servers page's rows: every remote with an https endpoint, and every server an import
/// recorded whose remote has since gone, so it can still be removed. Pure: everything read
/// from disk, the Windows store or the running local proxy is passed in.
/// </summary>
public static class ImportedServerList
{
    public const string TrustUpdateText = "Update ready (needs admin)";
    public const string NotTrustedText = "Not trusted";
    public const string NeverSyncedText = "Not since the local proxy started";

    public static IReadOnlyList<ImportedServerRow> Build(
        RoutingConfiguration routing,
        IReadOnlyList<ProxyConfigImportRecord> records,
        Func<string, string?> hostsAddress,
        Func<string, ServerTrustFacts> trust,
        IReadOnlyList<ClientTrustStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(hostsAddress);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(statuses);

        var servers = new List<(string Name, string Hostname)>();
        foreach (var remote in routing.Remotes)
        {
            if (remote.BaseAddress is { Scheme: "https" } address && DnsHostname.IsValid(DnsHostname.Normalize(address.Host)))
            {
                servers.Add((remote.Name, DnsHostname.Normalize(address.Host)));
            }
        }

        foreach (var record in records)
        {
            var hostname = DnsHostname.Normalize(record.Hostname);
            if (DnsHostname.IsValid(hostname) && servers.All(server => server.Hostname != hostname))
            {
                servers.Add((string.Empty, hostname));
            }
        }

        return servers
            .Select(server =>
            {
                var record = records.FirstOrDefault(entry => string.Equals(DnsHostname.Normalize(entry.Hostname), server.Hostname, StringComparison.Ordinal));
                var status = statuses.FirstOrDefault(entry => string.Equals(DnsHostname.Normalize(entry.Hostname), server.Hostname, StringComparison.Ordinal));
                var facts = trust(server.Hostname);
                var updateNeeded = facts.UpdateNeeded;
                return new ImportedServerRow(
                    server.Name,
                    server.Hostname,
                    hostsAddress(server.Hostname) ?? record?.Address ?? "(resolved by DNS)",
                    DescribeCertificate(facts, updateNeeded),
                    DescribeSync(status),
                    server.Name.Length == 0
                        ? 0
                        : routing.Routes.Count(route => string.Equals(routing.RemoteFor(route)?.Name, server.Name, StringComparison.OrdinalIgnoreCase)),
                    updateNeeded);
            })
            .OrderBy(row => row.Name.Length == 0)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Hostname, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The certificate status as the page shows it: no thumbprints, no subjects, just whether it is trusted.</summary>
    public static string DescribeCertificate(ServerTrustFacts facts, bool updateNeeded)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.PinnedCount == 0)
        {
            return NotTrustedText;
        }

        if (updateNeeded)
        {
            return TrustUpdateText;
        }

        return facts.TrustedUntil is { } until ? $"Trusted until {until.UtcDateTime:yyyy-MM-dd}" : NotTrustedText;
    }

    public static string DescribeSync(ClientTrustStatus? status) => status switch
    {
        null => NeverSyncedText,
        { LastError: { Length: > 0 } error, LastSynced: { } synced } => $"{synced.ToLocalTime():yyyy-MM-dd HH:mm}; the last try failed: {error}",
        { LastError: { Length: > 0 } error } => $"Failed: {error}",
        { LastSynced: { } synced } => synced.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
        _ => NeverSyncedText
    };
}
