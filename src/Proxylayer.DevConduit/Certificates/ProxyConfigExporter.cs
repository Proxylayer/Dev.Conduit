using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>What an export puts into a <c>.conduit</c> besides the certificates.</summary>
/// <param name="Routes">The server's routes, name to upstream origin.</param>
/// <param name="Scope">The delegated scope the server accepts, written into the file for clients to request; null writes none.</param>
public sealed record ProxyConfigExportRequest(
    string Hostname,
    string Address,
    int Port,
    string TenantId,
    string ClientId,
    IReadOnlyList<KeyValuePair<string, string>> Routes,
    string? Scope = null);

/// <summary>What an export wrote.</summary>
public sealed record ProxyConfigExportResult(string Path, ProxyConfigDocument Document, IssuedCertificateRecord ClientCertificate);

/// <summary>
/// Writes a <c>.conduit</c> for a client: brings the server's certificates up to date,
/// issues a new client certificate, and writes the file readable only by its owner and
/// Administrators, since it carries that certificate's private key.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProxyConfigExporter
{
    private readonly ServerCertificateAuthority _authority;
    private readonly TimeProvider _clock;

    public ProxyConfigExporter(ServerCertificateAuthority authority, TimeProvider clock)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ProxyConfigExportResult Export(ProxyConfigExportRequest request, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var hostname = DnsHostname.Normalize(request.Hostname);
        var set = _authority.Converge(hostname);
        var issued = _authority.IssueClientCertificate(hostname);

        ProxyConfigDocument document;
        using (var clientCertificate = issued.Certificate)
        {
            document = new ProxyConfigDocument(
                ProxyConfigDocument.CurrentFormat,
                hostname,
                request.Address.Trim(),
                request.Port,
                DnsHostname.FirstLabel(hostname),
                request.TenantId.Trim(),
                request.ClientId.Trim(),
                request.Routes.ToDictionary(route => route.Key, route => route.Value, StringComparer.OrdinalIgnoreCase),
                set.Published.Select(certificate => Convert.ToBase64String(certificate.RawData)).ToList(),
                Convert.ToBase64String(CertificateFactory.ExportPkcs12(clientCertificate)),
                _clock.GetUtcNow(),
                string.IsNullOrWhiteSpace(request.Scope) ? null : request.Scope.Trim());
        }

        // Written to a temporary file in the target folder and then moved into place, so a
        // failure leaves no partial file. A certificate that never reached a file is revoked.
        var path = Path.GetFullPath(outputPath);
        try
        {
            AtomicFile.Write(path, document.ToUtf8(), BuildSecurity());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (issued.Record.Enrollment is { } enrollment)
            {
                _authority.RevokeEnrollment(enrollment, ServerCertificateAuthority.UndeliveredRevocation);
            }

            throw;
        }

        return new ProxyConfigExportResult(path, document, issued.Record);
    }

    /// <summary>Full control for the account writing the file and for Administrators; nobody else, nothing inherited.</summary>
    public static FileSecurity BuildSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is { } owner)
        {
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
