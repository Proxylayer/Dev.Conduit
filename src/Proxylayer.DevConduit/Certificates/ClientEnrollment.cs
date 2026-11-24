using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>
/// One export, as <c>issued.json</c> records it. Every client certificate belongs to exactly
/// one enrollment: the one an export created, and every renewal of it after that. Revoking
/// the enrollment revokes all of them, those issued before and any a renewal would issue.
/// </summary>
/// <param name="Id">32 lower-case hex digits, random. Also the subject's OU in each of its certificates.</param>
/// <param name="Subject">The subject every certificate of the enrollment carries, and every renewal must ask for.</param>
/// <param name="Hostname">The server host name it was issued for.</param>
/// <param name="Authority">The thumbprint of the client authority that signs its certificates.</param>
/// <param name="CreatedAt">When the export created it.</param>
/// <param name="Revoked">True once revoked; no certificate of it is accepted from then on.</param>
/// <param name="RevokedReason">Why, for <c>certs list</c>.</param>
/// <param name="RevokedAt">When.</param>
public sealed record ClientEnrollment(
    string Id,
    string Subject,
    string Hostname,
    string Authority,
    DateTimeOffset CreatedAt,
    bool Revoked = false,
    string? RevokedReason = null,
    DateTimeOffset? RevokedAt = null);

/// <summary>Enrollment ids: creating one, and reading it from a certificate's subject. Pure.</summary>
public static partial class ClientEnrollmentId
{
    private const string OrganizationalUnitOid = "2.5.4.11";

    /// <summary>A new random id: 16 bytes as 32 lower-case hex digits.</summary>
    public static string New() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>True for an id as <see cref="New"/> makes it, in either case.</summary>
    public static bool IsValid(string? id) => id is not null && Pattern().IsMatch(id);

    /// <summary>The id in the form the ledger keeps: trimmed, lower case.</summary>
    public static string Normalize(string id) => id.Trim().ToLowerInvariant();

    /// <summary>The enrollment id a subject carries as its one OU, or null when it carries none or more than one.</summary>
    public static string? Of(X500DistinguishedName subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        string? found = null;
        foreach (var name in subject.EnumerateRelativeDistinguishedNames())
        {
            if (name.HasMultipleElements || name.GetSingleElementType().Value != OrganizationalUnitOid)
            {
                continue;
            }

            if (found is not null)
            {
                return null;
            }

            found = name.GetSingleElementValue();
        }

        return IsValid(found) ? Normalize(found!) : null;
    }

    [GeneratedRegex(@"\A[0-9a-fA-F]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
