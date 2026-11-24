using Proxylayer.DevConduit.Certificates;

namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// Settings bound from <c>DevConduit:Server</c>: the server's own host name, which turns on
/// the HTTPS endpoint with the app-managed certificate, its port, and the address written
/// into exported <c>.conduit</c> files. Configured in the machine settings file.
/// </summary>
public sealed class ServerHostOptions
{
    public const string SectionName = "DevConduit:Server";

    public const int DefaultPort = 8091;

    /// <summary>The name clients reach the server by. Empty leaves HTTPS unmanaged.</summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>The HTTPS port, on every interface.</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>The address clients map the host name to, written into exports. The export command can override it.</summary>
    public string ClientAddress { get; set; } = string.Empty;

    /// <summary>
    /// The Windows store the server trusts its own certificates in, <c>LocalMachine\Root</c> when
    /// blank, or <c>None</c> (<see cref="ServerSelfTrust"/>).
    /// </summary>
    public string TrustStore { get; set; } = string.Empty;

    /// <summary>True when a host name is configured, so the server manages its own certificate.</summary>
    public bool IsManaged => !string.IsNullOrWhiteSpace(Hostname);

    /// <summary>The host name as certificates and clients use it.</summary>
    public string NormalizedHostname => DnsHostname.Normalize(Hostname);

    /// <summary>The managed endpoint's URL.</summary>
    public string ListenUrl => $"https://0.0.0.0:{Port}";

    /// <summary>Why these settings cannot be used, or null when they can.</summary>
    public string? Problem()
    {
        if (!IsManaged)
        {
            return null;
        }

        if (!DnsHostname.IsValid(NormalizedHostname))
        {
            return $"{SectionName}:Hostname ('{Hostname}') is not a DNS host name. Give the name clients reach this server by, for example devconduit.example.internal.";
        }

        if (Port is < 1 or > 65535)
        {
            return $"{SectionName}:Port ({Port}) is not a TCP port.";
        }

        if (!string.IsNullOrWhiteSpace(ClientAddress) && !DnsHostname.IsAddressOrHostname(ClientAddress))
        {
            return $"{SectionName}:ClientAddress ('{ClientAddress}') is not an IP address or host name.";
        }

        return null;
    }
}
