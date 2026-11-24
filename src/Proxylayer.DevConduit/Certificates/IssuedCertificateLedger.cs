namespace Proxylayer.DevConduit.Certificates;

/// <summary>One client certificate the server issued, as recorded in <c>issued.json</c>.</summary>
/// <param name="Serial">The serial number, upper-case hex.</param>
/// <param name="Subject">The subject, for the person revoking it.</param>
/// <param name="NotAfter">End of validity.</param>
/// <param name="IssuedAt">When it was issued.</param>
/// <param name="Revoked">True once its enrollment was revoked; <c>/cert</c> refuses it from then on.</param>
/// <param name="SupersededBy">The serial of the latest certificate a renewal from it issued. A superseded certificate stays valid until it expires.</param>
/// <param name="Enrollment">The enrollment it belongs to. A record without one predates enrollments and is never accepted.</param>
public sealed record IssuedCertificateRecord(
    string Serial,
    string Subject,
    DateTimeOffset NotAfter,
    DateTimeOffset IssuedAt,
    bool Revoked = false,
    string? SupersededBy = null,
    string? Enrollment = null);

/// <summary>What revoking an enrollment did.</summary>
/// <param name="Enrollment">The enrollment revoked, or null for a record from before enrollments, revoked on its own.</param>
/// <param name="Serials">Every serial of it, now refused.</param>
public sealed record EnrollmentRevocation(string? Enrollment, IReadOnlyList<string> Serials);

/// <summary>The content of <c>issued.json</c>.</summary>
public sealed record IssuedCertificateLedgerDocument(
    IReadOnlyList<ClientEnrollment>? Enrollments,
    IReadOnlyList<IssuedCertificateRecord>? Certificates);

/// <summary>
/// The enrollments and client certificates the server has issued. Only a certificate recorded
/// here, in an enrollment that is recorded and not revoked, is accepted by <c>/cert</c>, so a
/// certificate the authority signed but that was never recorded is refused too. Revocation
/// covers a whole enrollment. Records are kept a year past expiry and then dropped, and an
/// enrollment goes with its last record.
/// </summary>
public sealed class IssuedCertificateLedger
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(365);

    private readonly List<IssuedCertificateRecord> _records;
    private readonly List<ClientEnrollment> _enrollments;

    public IssuedCertificateLedger(IEnumerable<IssuedCertificateRecord> records, IEnumerable<ClientEnrollment>? enrollments = null)
    {
        _records = records.Where(record => !string.IsNullOrWhiteSpace(record.Serial)).ToList();
        _enrollments = (enrollments ?? []).Where(enrollment => ClientEnrollmentId.IsValid(enrollment.Id)).ToList();
    }

    public IReadOnlyList<IssuedCertificateRecord> Records => _records;

    public IReadOnlyList<ClientEnrollment> Enrollments => _enrollments;

    public IssuedCertificateLedgerDocument ToDocument() => new(_enrollments, _records);

    /// <summary>Serial numbers compare as upper-case hex, as <see cref="System.Security.Cryptography.X509Certificates.X509Certificate2.SerialNumber"/> gives them.</summary>
    public static string NormalizeSerial(string serial) => serial.Trim().Replace(":", string.Empty).Replace(" ", string.Empty).ToUpperInvariant();

    public IssuedCertificateRecord? Find(string serial)
    {
        var normalized = NormalizeSerial(serial);
        return _records.FirstOrDefault(record => string.Equals(NormalizeSerial(record.Serial), normalized, StringComparison.Ordinal));
    }

    public ClientEnrollment? FindEnrollment(string? id) =>
        id is null ? null : _enrollments.FirstOrDefault(enrollment => SameId(enrollment.Id, id));

    /// <summary>The certificates of an enrollment, oldest first.</summary>
    public IReadOnlyList<IssuedCertificateRecord> CertificatesOf(string id) =>
        _records.Where(record => SameId(record.Enrollment, id)).OrderBy(record => record.IssuedAt).ToList();

    /// <summary>True when the serial was issued here in an enrollment that is not revoked.</summary>
    public bool IsAccepted(string serial) =>
        Find(serial) is { Revoked: false, Enrollment: { } id } && FindEnrollment(id) is { Revoked: false };

    /// <summary>True when the serial was issued here in <paramref name="enrollment"/>, and that enrollment is not revoked.</summary>
    public bool IsAccepted(string serial, string enrollment) =>
        IsAccepted(serial) && SameId(Find(serial)!.Enrollment, enrollment);

    /// <summary>Records a new enrollment and its first certificate.</summary>
    public void Enroll(ClientEnrollment enrollment, IssuedCertificateRecord record, DateTimeOffset now)
    {
        var id = ClientEnrollmentId.Normalize(enrollment.Id);
        if (!ClientEnrollmentId.IsValid(id) || FindEnrollment(id) is not null)
        {
            throw new ArgumentException($"'{enrollment.Id}' is not a new enrollment id.", nameof(enrollment));
        }

        _enrollments.Add(enrollment with { Id = id });
        RecordIssue(record with { Enrollment = id }, now);
    }

    /// <summary>Records an issue, dropping records that expired more than a year ago and enrollments left without any.</summary>
    public void RecordIssue(IssuedCertificateRecord record, DateTimeOffset now)
    {
        _records.RemoveAll(existing => existing.NotAfter + Retention < now);
        _records.Add(record with
        {
            Serial = NormalizeSerial(record.Serial),
            Enrollment = record.Enrollment is null ? null : ClientEnrollmentId.Normalize(record.Enrollment)
        });
        _enrollments.RemoveAll(enrollment => !_records.Any(existing => SameId(existing.Enrollment, enrollment.Id)));
    }

    /// <summary>Marks <paramref name="serial"/> as replaced by <paramref name="replacement"/>.</summary>
    public void MarkSuperseded(string serial, string replacement) =>
        Replace(serial, record => record with { SupersededBy = NormalizeSerial(replacement) });

    /// <summary>Revokes the enrollment <paramref name="serial"/> belongs to: every serial in it. Null when the serial was never issued here.</summary>
    public EnrollmentRevocation? Revoke(string serial, string reason, DateTimeOffset now)
    {
        var record = Find(serial);
        if (record is null)
        {
            return null;
        }

        if (record.Enrollment is null)
        {
            Replace(record.Serial, existing => existing with { Revoked = true });
            return new EnrollmentRevocation(null, [record.Serial]);
        }

        return RevokeEnrollment(record.Enrollment, reason, now);
    }

    /// <summary>Revokes an enrollment and every serial in it. Null when there is no such enrollment.</summary>
    public EnrollmentRevocation? RevokeEnrollment(string id, string reason, DateTimeOffset now)
    {
        var index = _enrollments.FindIndex(enrollment => SameId(enrollment.Id, id));
        if (index < 0)
        {
            return null;
        }

        var enrollment = _enrollments[index];
        if (!enrollment.Revoked)
        {
            _enrollments[index] = enrollment with { Revoked = true, RevokedReason = reason, RevokedAt = now };
        }

        var serials = new List<string>();
        for (var position = 0; position < _records.Count; position++)
        {
            if (SameId(_records[position].Enrollment, enrollment.Id))
            {
                _records[position] = _records[position] with { Revoked = true };
                serials.Add(_records[position].Serial);
            }
        }

        return new EnrollmentRevocation(enrollment.Id, serials);
    }

    /// <summary>
    /// Revokes every enrollment not signed by <paramref name="authorityThumbprint"/>, and every
    /// record from before enrollments, for a new client authority. Returns how many enrollments
    /// were newly revoked.
    /// </summary>
    public int RevokeAllExcept(string authorityThumbprint, string reason, DateTimeOffset now)
    {
        var revoked = 0;
        foreach (var enrollment in _enrollments.ToList())
        {
            if (!enrollment.Revoked && !string.Equals(enrollment.Authority, authorityThumbprint, StringComparison.OrdinalIgnoreCase))
            {
                RevokeEnrollment(enrollment.Id, reason, now);
                revoked++;
            }
        }

        for (var position = 0; position < _records.Count; position++)
        {
            if (_records[position] is { Enrollment: null, Revoked: false })
            {
                _records[position] = _records[position] with { Revoked = true };
            }
        }

        return revoked;
    }

    private static bool SameId(string? left, string? right) =>
        left is not null && right is not null && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private bool Replace(string serial, Func<IssuedCertificateRecord, IssuedCertificateRecord> change)
    {
        var normalized = NormalizeSerial(serial);
        var index = _records.FindIndex(record => string.Equals(NormalizeSerial(record.Serial), normalized, StringComparison.Ordinal));
        if (index < 0)
        {
            return false;
        }

        _records[index] = change(_records[index]);
        return true;
    }
}
