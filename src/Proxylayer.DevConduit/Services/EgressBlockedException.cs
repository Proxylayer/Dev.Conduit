using System.Net;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Raised when an outbound connection is refused because the destination resolves into a
/// network listed in <c>DevConduit:BlockedEgressNetworks</c>. An <see cref="IOException"/> so
/// that code treating I/O failures as transport failures keeps doing so.
/// </summary>
public sealed class EgressBlockedException : IOException
{
    public EgressBlockedException(string host, IPAddress blockedAddress)
        : base(
            $"The destination '{host}' resolves to {blockedAddress}, which is blocked by " +
            "DevConduit:BlockedEgressNetworks. Dev.Conduit refuses to connect to it.")
    {
        Host = host;
        BlockedAddress = blockedAddress;
    }

    /// <summary>The host name or address literal the caller asked to connect to.</summary>
    public string Host { get; }

    /// <summary>The resolved address that falls inside a blocked network.</summary>
    public IPAddress BlockedAddress { get; }
}
