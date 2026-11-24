using System.Security.Cryptography.X509Certificates;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>The certificates a client pins for one server: <c>trust.json</c> in its client folder.</summary>
public sealed record ClientTrustDocument(string Hostname, IReadOnlyList<PublishedServerCertificate> Certificates);

/// <summary>
/// Merging and reading pinned server certificates. Pure: the clock is passed in, and nothing
/// is read from or written to disk here.
/// </summary>
public static class PinnedTrust
{
    /// <summary>A certificate in its pinned form.</summary>
    public static PublishedServerCertificate Pin(X509Certificate2 certificate) =>
        new(
            Convert.ToBase64String(certificate.RawData),
            certificate.NotBefore.ToUniversalTime(),
            certificate.NotAfter.ToUniversalTime(),
            certificate.Thumbprint);

    /// <summary>
    /// The pins to keep: everything already pinned and everything offered, one entry per
    /// thumbprint, without any that have expired, newest expiry first.
    /// </summary>
    public static IReadOnlyList<PublishedServerCertificate> Merge(
        IEnumerable<PublishedServerCertificate> existing,
        IEnumerable<PublishedServerCertificate> offered,
        DateTimeOffset now) =>
        existing.Concat(offered)
            .Where(pin => pin.NotAfter > now)
            .GroupBy(pin => pin.Thumbprint, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First() with { Thumbprint = group.Key.ToUpperInvariant() })
            .OrderByDescending(pin => pin.NotAfter)
            .ToList();

    /// <summary>
    /// The effective pins for one server: every pin of the machine layer and of the user's own
    /// layer, one entry per thumbprint, the machine's entry first. Expired pins are kept; the
    /// relay checks validity on every handshake.
    /// </summary>
    public static IReadOnlyList<PublishedServerCertificate> Union(
        IEnumerable<PublishedServerCertificate> machine,
        IEnumerable<PublishedServerCertificate> user) =>
        machine.Concat(user)
            .GroupBy(pin => pin.Thumbprint, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

    /// <summary>
    /// The offered certificates a client may pin for <paramref name="hostname"/>: parsed, their
    /// thumbprint agreeing with their content, passing <see cref="ServerCertificateRules"/>, and
    /// not expired. Each one refused for a rule adds a line to <paramref name="problems"/>.
    /// </summary>
    public static IReadOnlyList<PublishedServerCertificate> Acceptable(
        IEnumerable<PublishedServerCertificate> offered,
        string hostname,
        DateTimeOffset now,
        ICollection<string>? problems = null)
    {
        var accepted = new List<PublishedServerCertificate>();
        foreach (var certificate in Load(offered))
        {
            using (certificate)
            {
                if (ServerCertificateRules.Problem(certificate, hostname) is { } problem)
                {
                    problems?.Add(problem);
                    continue;
                }

                if (now < certificate.NotAfter.ToUniversalTime())
                {
                    accepted.Add(Pin(certificate));
                }
            }
        }

        return accepted;
    }

    /// <summary>The pins as certificates. An entry that does not parse, or whose thumbprint disagrees with its content, is skipped.</summary>
    public static IReadOnlyList<X509Certificate2> Load(IEnumerable<PublishedServerCertificate> pins)
    {
        var certificates = new List<X509Certificate2>();
        foreach (var pin in pins)
        {
            try
            {
                var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(pin.Der));
                if (string.Equals(certificate.Thumbprint, pin.Thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    certificates.Add(certificate);
                }
                else
                {
                    certificate.Dispose();
                }
            }
            catch (Exception exception) when (exception is FormatException or System.Security.Cryptography.CryptographicException)
            {
                // A damaged entry is ignored rather than trusted or allowed to break the rest.
            }
        }

        return certificates;
    }
}
