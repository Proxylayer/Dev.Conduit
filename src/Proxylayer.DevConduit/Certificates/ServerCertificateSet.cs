using System.Security.Cryptography.X509Certificates;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>
/// The server's certificates as loaded from the store: the one it serves, the next one when a
/// rotation is under way, and the authority that signs client certificates. Each carries its
/// private key. Immutable; a rotation produces a new set.
/// </summary>
public sealed class ServerCertificateSet
{
    public ServerCertificateSet(string hostname, X509Certificate2 current, X509Certificate2? next, X509Certificate2 clientAuthority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        Hostname = hostname;
        Current = current ?? throw new ArgumentNullException(nameof(current));
        Next = next;
        ClientAuthority = clientAuthority ?? throw new ArgumentNullException(nameof(clientAuthority));
    }

    public string Hostname { get; }

    /// <summary>The certificate Kestrel serves.</summary>
    public X509Certificate2 Current { get; }

    /// <summary>The certificate the server switches to next, published early so clients can pin it.</summary>
    public X509Certificate2? Next { get; }

    public X509Certificate2 ClientAuthority { get; }

    /// <summary>The certificates clients should trust, current first, without private keys.</summary>
    public IReadOnlyList<X509Certificate2> Published =>
        Next is null ? [Current] : [Current, Next];
}
