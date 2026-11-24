using System.Net;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Raised when an outbound connection is refused because <c>DevConduit:Route:AllowedTargets</c>
/// does not cover the destination, the name asked for or every address it resolved to. An
/// <see cref="IOException"/>, like <see cref="EgressBlockedException"/>, so that code treating
/// I/O failures as transport failures keeps doing so.
/// </summary>
public sealed class TargetNotAllowedException : IOException
{
    public TargetNotAllowedException(string host, int port, IReadOnlyCollection<IPAddress> addresses)
        : base(Describe(host, port, addresses))
    {
        Host = host;
        Port = port;
    }

    /// <summary>The host name or address literal the caller asked to connect to.</summary>
    public string Host { get; }

    public int Port { get; }

    private static string Describe(string host, int port, IReadOnlyCollection<IPAddress> addresses)
    {
        var resolved = addresses.Count == 0 || (addresses.Count == 1 && IPAddress.TryParse(host.Trim('[', ']'), out _))
            ? string.Empty
            : $" (resolved to {string.Join(", ", addresses)})";
        return $"The destination '{host}:{port}'{resolved} is not covered by {TargetAllowList.SettingKey}. "
               + "Dev.Conduit refuses to connect to it; ask the server's administrator to add it.";
    }
}
