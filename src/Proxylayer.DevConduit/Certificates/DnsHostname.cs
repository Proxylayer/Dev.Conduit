using System.Net;
using System.Text.RegularExpressions;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>
/// The host names Dev.Conduit issues certificates for, writes into a hosts file and names
/// client folders after: plain DNS names of letters, digits and dashes, compared and stored
/// in lower case. An IP address is not a host name here.
/// </summary>
public static partial class DnsHostname
{
    private const int MaxLength = 253;

    /// <summary>True when <paramref name="value"/> is a DNS host name, not an IP address.</summary>
    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaxLength
        && !IPAddress.TryParse(value, out _)
        && Name().IsMatch(value);

    /// <summary>The name in the form Dev.Conduit stores it: trimmed, without a trailing dot, lower case.</summary>
    public static string Normalize(string? value) => (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>The first label, which names the remote a <c>.conduit</c> import adds.</summary>
    public static string FirstLabel(string hostname)
    {
        var normalized = Normalize(hostname);
        var dot = normalized.IndexOf('.');
        return dot < 0 ? normalized : normalized[..dot];
    }

    /// <summary>True when <paramref name="value"/> is an IP address or a DNS host name.</summary>
    public static bool IsAddressOrHostname(string? value) =>
        !string.IsNullOrWhiteSpace(value) && (IPAddress.TryParse(value.Trim(), out _) || IsValid(Normalize(value)));

    [GeneratedRegex(@"\A(?=.{1,253}\z)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Name();
}
