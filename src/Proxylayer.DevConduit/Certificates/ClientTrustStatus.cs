using System.Collections.Concurrent;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>Where one imported server stands, for the tray to show and act on.</summary>
/// <param name="Hostname">The server.</param>
/// <param name="PinnedCertificates">How many server certificates are pinned.</param>
/// <param name="RootStoreUpdateNeeded">
/// True when a pinned certificate is not in <c>LocalMachine\Root</c>. The relay works without
/// it; browsers and other programs on this machine do not. <c>service-admin trust &lt;hostname&gt;</c> fixes it.
/// </param>
/// <param name="ClientCertificateNotAfter">When this machine's client certificate for the server expires.</param>
/// <param name="LastSynced">When <c>/cert</c> last answered.</param>
/// <param name="LastError">Why the last sync failed, or null.</param>
/// <param name="MachineIdentityRenewalDue">
/// True when the machine layer's client certificate is inside its renewal window
/// (<see cref="ClientTrustLayers.MachineIdentityRenewalDue"/>). Each user's relay renews its own
/// without help; the machine's, which new users and <c>service-admin trust</c> authenticate
/// with, renews only through <c>service-admin trust &lt;hostname&gt;</c>.
/// </param>
public sealed record ClientTrustStatus(
    string Hostname,
    int PinnedCertificates,
    bool RootStoreUpdateNeeded,
    DateTimeOffset? ClientCertificateNotAfter,
    DateTimeOffset? LastSynced,
    string? LastError,
    bool MachineIdentityRenewalDue = false)
{
    /// <summary>
    /// True when <c>service-admin trust &lt;hostname&gt;</c> has something to do: a pinned
    /// certificate to add to Windows trust, or the machine identity to renew.
    /// </summary>
    public bool UpdateNeeded => RootStoreUpdateNeeded || MachineIdentityRenewalDue;
}

/// <summary>The latest <see cref="ClientTrustStatus"/> for each imported server, as the sync finds it.</summary>
public sealed class ClientTrustStatusBoard
{
    private readonly ConcurrentDictionary<string, ClientTrustStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after a status changes, on the sync's thread.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<ClientTrustStatus> Current => _statuses.Values.OrderBy(status => status.Hostname, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>True when any imported server has a pinned certificate missing from Windows trust.</summary>
    public bool RootStoreUpdateNeeded => _statuses.Values.Any(status => status.RootStoreUpdateNeeded);

    /// <summary>True when any imported server needs <c>service-admin trust</c> (<see cref="ClientTrustStatus.UpdateNeeded"/>).</summary>
    public bool UpdateNeeded => _statuses.Values.Any(status => status.UpdateNeeded);

    public void Set(ClientTrustStatus status)
    {
        _statuses[status.Hostname] = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
