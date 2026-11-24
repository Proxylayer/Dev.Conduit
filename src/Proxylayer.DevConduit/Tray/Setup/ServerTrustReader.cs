using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Proxylayer.DevConduit.Certificates;

namespace Proxylayer.DevConduit.Tray.Setup;

/// <summary>
/// Reads <see cref="ServerTrustFacts"/> for an imported server: its effective pinned certificates,
/// the machine layer's and this user's together (<see cref="ClientTrustLayers"/>), and which of
/// them the Windows store holds, and whether the machine identity is due for renewal. All read
/// without administrator rights.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServerTrustReader
{
    private readonly ClientTrustLayers _layers;
    private readonly StoreName _storeName;
    private readonly StoreLocation _storeLocation;

    public ServerTrustReader(ClientTrustLayers layers, StoreName storeName = StoreName.Root, StoreLocation storeLocation = StoreLocation.LocalMachine)
    {
        _layers = layers ?? throw new ArgumentNullException(nameof(layers));
        _storeName = storeName;
        _storeLocation = storeLocation;
    }

    public ServerTrustFacts Read(string hostname, DateTimeOffset now)
    {
        if (!DnsHostname.IsValid(DnsHostname.Normalize(hostname)))
        {
            return ServerTrustFacts.None;
        }

        var renewalDue = _layers.MachineIdentityRenewalDue(hostname, now);
        var pins = PinnedTrust.Load(_layers.Pins(hostname));
        try
        {
            var valid = pins.Where(pin => pin.NotBefore.ToUniversalTime() <= now && now < pin.NotAfter.ToUniversalTime()).ToList();
            if (valid.Count == 0)
            {
                return ServerTrustFacts.None with { MachineIdentityRenewalDue = renewalDue };
            }

            IReadOnlyList<X509Certificate2> missing;
            try
            {
                missing = new RootStoreTrust(_layers.Machine.RootStoreRecordPath, _storeName, _storeLocation).Missing(valid);
            }
            catch (CryptographicException)
            {
                missing = valid;
            }

            var trusted = valid.Where(pin => !missing.Contains(pin)).ToList();
            DateTimeOffset? until = trusted.Count == 0 ? null : trusted.Max(pin => new DateTimeOffset(pin.NotAfter.ToUniversalTime()));
            return new ServerTrustFacts(valid.Count, until, missing.Count > 0, renewalDue);
        }
        finally
        {
            foreach (var pin in pins)
            {
                pin.Dispose();
            }
        }
    }
}
