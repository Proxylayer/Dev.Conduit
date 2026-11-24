using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>A certificate the app added to the Windows store, as the root store record keeps it.</summary>
public sealed record AddedRootCertificate(string Thumbprint, string Hostname, DateTimeOffset NotAfter);

/// <summary>What one synchronization of the Windows store did.</summary>
public sealed record RootStoreChange(IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> AlreadyPresent);

/// <summary>
/// Puts a server's certificates into Windows trust, <c>LocalMachine\Root</c> unless told
/// otherwise, and takes out the ones it put there that have expired or are no longer wanted.
/// Every certificate it adds is recorded in its record file (<c>root-store.json</c>), and only
/// recorded certificates are ever removed, so nothing anyone else trusted is touched. Every
/// certificate must pass <see cref="ServerCertificateRules"/> first. A workstation uses it for
/// the servers it imported, a server for its own certificates (<see cref="ServerSelfTrust"/>),
/// each with a record of its own. Needs administrator rights for the machine store.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RootStoreTrust
{
    private readonly Func<OpenFlags, X509Store> _open;
    private readonly string _recordPath;

    public RootStoreTrust(string recordPath, StoreName storeName = StoreName.Root, StoreLocation storeLocation = StoreLocation.LocalMachine)
        : this(recordPath, $"{storeLocation}\\{storeName}", flags => Open(new X509Store(storeName, storeLocation), flags))
    {
    }

    /// <param name="recordPath">The record of the certificates this app added.</param>
    /// <param name="storeDescription">How messages name the store.</param>
    /// <param name="openStore">Opens the store with the flags given; for a store other than a fixed Windows one, or one that fails.</param>
    public RootStoreTrust(string recordPath, string storeDescription, Func<OpenFlags, X509Store> openStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDescription);
        _recordPath = recordPath;
        StoreDescription = storeDescription;
        _open = openStore ?? throw new ArgumentNullException(nameof(openStore));
    }

    public string StoreDescription { get; }

    /// <summary>
    /// Adds each of <paramref name="pins"/> not yet in the store, and removes the ones this app
    /// added for <paramref name="hostname"/> that are expired or not in <paramref name="pins"/>,
    /// and with <paramref name="removeOtherHostnames"/> the ones it added for any other host name
    /// too, for a record that only ever holds one server's certificates.
    /// </summary>
    /// <exception cref="InvalidOperationException">A certificate fails the server certificate rules; nothing is changed.</exception>
    public RootStoreChange Synchronize(string hostname, IReadOnlyList<X509Certificate2> pins, DateTimeOffset now, bool removeOtherHostnames = false)
    {
        hostname = DnsHostname.Normalize(hostname);
        foreach (var pin in pins)
        {
            if (ServerCertificateRules.Problem(pin, hostname) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }
        }

        var current = pins.Where(pin => now < pin.NotAfter.ToUniversalTime()).ToList();
        var record = ReadRecord();
        var added = new List<string>();
        var removed = new List<string>();
        var present = new List<string>();

        using var store = _open(OpenFlags.ReadWrite);

        foreach (var pin in current)
        {
            if (store.Certificates.Find(X509FindType.FindByThumbprint, pin.Thumbprint, validOnly: false).Count > 0)
            {
                present.Add(pin.Thumbprint);
                continue;
            }

            using var publicOnly = X509CertificateLoader.LoadCertificate(pin.RawData);
            publicOnly.FriendlyName = $"Dev.Conduit {hostname}";
            store.Add(publicOnly);
            added.Add(pin.Thumbprint);
            record.RemoveAll(entry => string.Equals(entry.Thumbprint, pin.Thumbprint, StringComparison.OrdinalIgnoreCase));
            record.Add(new AddedRootCertificate(pin.Thumbprint, hostname, pin.NotAfter.ToUniversalTime()));
        }

        var keep = current.Select(pin => pin.Thumbprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = record.Where(entry => string.Equals(entry.Hostname, hostname, StringComparison.OrdinalIgnoreCase)
            ? entry.NotAfter <= now || !keep.Contains(entry.Thumbprint)
            : removeOtherHostnames);
        removed.AddRange(Remove(store, record, stale.ToList()));

        WriteRecord(record);
        return new RootStoreChange(added, removed, present);
    }

    /// <summary>Removes every certificate this record says the app added, for any host name. Returns their thumbprints.</summary>
    public IReadOnlyList<string> RemoveAll()
    {
        var record = ReadRecord();
        if (record.Count == 0)
        {
            return [];
        }

        using var store = _open(OpenFlags.ReadWrite);
        var removed = Remove(store, record, record.ToList());
        WriteRecord(record);
        return removed;
    }

    /// <summary>The certificates this record says the app added, for every host name.</summary>
    public IReadOnlyList<AddedRootCertificate> Added() => ReadRecord();

    /// <summary>The pinned certificates for a host that the Windows store does not hold yet. Reading needs no administrator rights.</summary>
    public IReadOnlyList<X509Certificate2> Missing(IReadOnlyList<X509Certificate2> pins)
    {
        using var store = _open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return pins
            .Where(pin => store.Certificates.Find(X509FindType.FindByThumbprint, pin.Thumbprint, validOnly: false).Count == 0)
            .ToList();
    }

    /// <summary>The certificates this app recorded adding for <paramref name="hostname"/>.</summary>
    public IReadOnlyList<AddedRootCertificate> AddedFor(string hostname)
    {
        hostname = DnsHostname.Normalize(hostname);
        return ReadRecord().Where(entry => string.Equals(entry.Hostname, hostname, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static X509Store Open(X509Store store, OpenFlags flags)
    {
        try
        {
            store.Open(flags);
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    /// <summary>Takes <paramref name="entries"/> out of the store and out of <paramref name="record"/>.</summary>
    private static List<string> Remove(X509Store store, List<AddedRootCertificate> record, IReadOnlyList<AddedRootCertificate> entries)
    {
        var removed = new List<string>();
        foreach (var entry in entries)
        {
            foreach (var match in store.Certificates.Find(X509FindType.FindByThumbprint, entry.Thumbprint, validOnly: false))
            {
                store.Remove(match);
                match.Dispose();
            }

            removed.Add(entry.Thumbprint);
            record.Remove(entry);
        }

        return removed;
    }

    private List<AddedRootCertificate> ReadRecord()
    {
        if (!File.Exists(_recordPath))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<AddedRootCertificate>>(File.ReadAllText(_recordPath), StorageJson.Options) ?? [];
    }

    private void WriteRecord(List<AddedRootCertificate> record) =>
        AtomicFile.Write(_recordPath, JsonSerializer.SerializeToUtf8Bytes(record, StorageJson.Options));
}
