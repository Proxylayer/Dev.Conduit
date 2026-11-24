using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>
/// The server certificates the relay pins, by host name: the effective pins of
/// <see cref="ClientTrustLayers"/>, both layers' <c>trust.json</c> together, cached until either
/// file changes. Read on every TLS handshake to a remote whose certificate Windows does not
/// already trust, so it stays cheap: two file time checks per lookup.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClientTrustStore
{
    private readonly ClientTrustLayers _layers;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public ClientTrustStore(ClientTrustLayers layers)
    {
        _layers = layers ?? throw new ArgumentNullException(nameof(layers));
    }

    public ClientTrustLayers Layers => _layers;

    /// <summary>The certificates pinned for <paramref name="hostname"/> in either layer; empty when it was never imported.</summary>
    public IReadOnlyList<X509Certificate2> PinsFor(string hostname)
    {
        var normalized = DnsHostname.Normalize(hostname);
        if (!DnsHostname.IsValid(normalized))
        {
            return [];
        }

        var machine = _layers.MachineFolder(normalized);
        var user = _layers.UserFolder(normalized);
        DateTime machineWritten;
        DateTime userWritten;
        try
        {
            machineWritten = File.GetLastWriteTimeUtc(machine.TrustPath);
            userWritten = user is null ? default : File.GetLastWriteTimeUtc(user.TrustPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        if (_entries.TryGetValue(normalized, out var cached) && cached.MachineWritten == machineWritten && cached.UserWritten == userWritten)
        {
            return cached.Certificates;
        }

        var loaded = new Entry(machineWritten, userWritten, PinnedTrust.Load(_layers.Pins(normalized)));
        _entries[normalized] = loaded;
        return loaded.Certificates;
    }

    private sealed record Entry(DateTime MachineWritten, DateTime UserWritten, IReadOnlyList<X509Certificate2> Certificates);
}
