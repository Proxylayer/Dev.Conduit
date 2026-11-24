namespace Proxylayer.DevConduit.Certificates;

/// <summary>A server certificate as <c>GET /cert</c> publishes it.</summary>
/// <param name="Der">The certificate, DER encoded, base64.</param>
public sealed record PublishedServerCertificate(string Der, DateTimeOffset NotBefore, DateTimeOffset NotAfter, string Thumbprint);

/// <summary>What <c>GET /cert</c> says about the client certificate that called it.</summary>
/// <param name="RenewAfter">From when the client should renew it through <c>POST /cert/renew</c>.</param>
public sealed record ClientCertificateStatus(DateTimeOffset NotAfter, string Serial, DateTimeOffset RenewAfter);

/// <summary>The body of <c>GET /cert</c>.</summary>
public sealed record CertResponse(
    string Hostname,
    IReadOnlyList<PublishedServerCertificate> ServerCertificates,
    ClientCertificateStatus ClientCertificate);

/// <summary>The body of <c>POST /cert/renew</c>.</summary>
/// <param name="Csr">A PKCS#10 request, DER encoded, base64.</param>
public sealed record CertRenewRequest(string Csr);

/// <summary>The answer to <c>POST /cert/renew</c>.</summary>
/// <param name="Certificate">The new client certificate, DER encoded, base64.</param>
public sealed record CertRenewResponse(string Certificate, string Serial, DateTimeOffset NotAfter);

/// <summary>When a client renews its certificate: this long before it expires.</summary>
public static class ClientCertificateRenewal
{
    public static readonly TimeSpan RenewWithin = TimeSpan.FromDays(60);

    public static DateTimeOffset RenewAfter(DateTimeOffset notAfter) => notAfter - RenewWithin;

    public static bool IsDue(DateTimeOffset notAfter, DateTimeOffset now) => now >= RenewAfter(notAfter);

    /// <summary>Due and not yet expired: the server renews only a certificate still inside its validity period.</summary>
    public static bool IsRenewable(DateTimeOffset notAfter, DateTimeOffset now) => IsDue(notAfter, now) && now < notAfter;
}
