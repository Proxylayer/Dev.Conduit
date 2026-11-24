using Proxylayer.DevConduit.Certificates;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>
/// Recording enrollments and the client certificates issued in them, superseding and revoking,
/// and finding them by serial. Revocation covers a whole enrollment.
/// </summary>
public sealed class IssuedCertificateLedgerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private const string First = "11111111111111111111111111111111";
    private const string Second = "22222222222222222222222222222222";
    private const string Authority = "AUTHORITY";

    private static IssuedCertificateRecord Record(string serial, DateTimeOffset? notAfter = null, string? enrollment = First) =>
        new(serial, "CN=host client 20260601", notAfter ?? Now.AddDays(365), Now, Enrollment: enrollment);

    private static ClientEnrollment Enrollment(string id = First, string authority = Authority) =>
        new(id, $"CN=host client 20260601, OU={id}", "host", authority, Now);

    private static IssuedCertificateLedger Ledger(params IssuedCertificateRecord[] records) =>
        new(records, [Enrollment(First), Enrollment(Second)]);

    [Theory]
    [InlineData("0a1b2c", "0A1B2C")]
    [InlineData(" 0a:1b:2c ", "0A1B2C")]
    [InlineData("0a 1b 2c", "0A1B2C")]
    [InlineData("0A1B2C", "0A1B2C")]
    public void NormalizeSerial_IsUpperCaseHexWithoutSeparators(string serial, string expected) =>
        Assert.Equal(expected, IssuedCertificateLedger.NormalizeSerial(serial));

    [Fact]
    public void Constructor_DropsRecordsWithoutASerialAndEnrollmentsWithoutAValidId()
    {
        var ledger = new IssuedCertificateLedger(
            [Record("01"), Record(""), Record("  "), Record("02")],
            [Enrollment(), Enrollment("not-an-id")]);

        Assert.Equal(["01", "02"], ledger.Records.Select(record => record.Serial));
        Assert.Equal([First], ledger.Enrollments.Select(enrollment => enrollment.Id));
    }

    [Fact]
    public void RecordIssue_StoresTheSerialNormalized()
    {
        var ledger = Ledger();

        ledger.RecordIssue(Record("ab:cd"), Now);

        Assert.Equal("ABCD", Assert.Single(ledger.Records).Serial);
    }

    [Fact]
    public void Find_MatchesIgnoringCaseAndSeparators()
    {
        var ledger = Ledger(Record("ABCD"));

        Assert.NotNull(ledger.Find("abcd"));
        Assert.NotNull(ledger.Find("ab:cd"));
        Assert.Null(ledger.Find("ABCE"));
    }

    [Fact]
    public void IsAccepted_IsTrueForAnIssuedSerialOfAKnownEnrollmentOnly()
    {
        var ledger = Ledger(Record("01"), Record("03", enrollment: null), Record("04", enrollment: "33333333333333333333333333333333"));

        Assert.True(ledger.IsAccepted("01"));
        Assert.False(ledger.IsAccepted("02"));
        Assert.False(ledger.IsAccepted("03"));
        Assert.False(ledger.IsAccepted("04"));
    }

    [Fact]
    public void IsAccepted_WithAnEnrollment_NeedsTheRecordToBelongToIt()
    {
        var ledger = Ledger(Record("01"));

        Assert.True(ledger.IsAccepted("01", First));
        Assert.True(ledger.IsAccepted("01", First.ToUpperInvariant()));
        Assert.False(ledger.IsAccepted("01", Second));
    }

    [Fact]
    public void Enroll_RecordsTheEnrollmentAndItsFirstCertificate()
    {
        var ledger = new IssuedCertificateLedger([]);

        ledger.Enroll(Enrollment(First.ToUpperInvariant()), Record("01", enrollment: null), Now);

        Assert.Equal(First, Assert.Single(ledger.Enrollments).Id);
        Assert.Equal(First, Assert.Single(ledger.Records).Enrollment);
        Assert.True(ledger.IsAccepted("01", First));
    }

    [Fact]
    public void Enroll_RefusesAnIdInUse()
    {
        var ledger = Ledger(Record("01"));

        Assert.Throws<ArgumentException>(() => ledger.Enroll(Enrollment(First), Record("02"), Now));
    }

    [Fact]
    public void Revoke_RevokesEverySerialOfTheEnrollment()
    {
        var ledger = Ledger(Record("01"), Record("02"), Record("03", enrollment: Second));

        var revocation = ledger.Revoke("01", "leaked", Now);

        Assert.Equal(First, revocation!.Enrollment);
        Assert.Equal(["01", "02"], revocation.Serials);
        Assert.False(ledger.IsAccepted("01"));
        Assert.False(ledger.IsAccepted("02"));
        Assert.True(ledger.Find("02")!.Revoked);
        Assert.True(ledger.IsAccepted("03"));
        var enrollment = ledger.FindEnrollment(First)!;
        Assert.True(enrollment.Revoked);
        Assert.Equal("leaked", enrollment.RevokedReason);
        Assert.Equal(Now, enrollment.RevokedAt);
    }

    [Fact]
    public void Revoke_IsNullForASerialNeverIssued()
    {
        var ledger = Ledger(Record("01"));

        Assert.Null(ledger.Revoke("99", "leaked", Now));
        Assert.False(ledger.Find("01")!.Revoked);
    }

    [Fact]
    public void Revoke_TwiceStaysRevokedAndKeepsTheFirstReason()
    {
        var ledger = Ledger(Record("01"));

        Assert.NotNull(ledger.Revoke("01", "first", Now));
        Assert.NotNull(ledger.Revoke("01", "second", Now.AddDays(1)));

        Assert.False(ledger.IsAccepted("01"));
        Assert.Equal("first", ledger.FindEnrollment(First)!.RevokedReason);
    }

    [Fact]
    public void Revoke_OfARecordFromBeforeEnrollments_RevokesItAlone()
    {
        var ledger = Ledger(Record("01", enrollment: null), Record("02"));

        var revocation = ledger.Revoke("01", "leaked", Now);

        Assert.Null(revocation!.Enrollment);
        Assert.Equal(["01"], revocation.Serials);
        Assert.True(ledger.Find("01")!.Revoked);
        Assert.True(ledger.IsAccepted("02"));
    }

    [Fact]
    public void RevokeEnrollment_IsNullForAnUnknownId() =>
        Assert.Null(Ledger(Record("01")).RevokeEnrollment("33333333333333333333333333333333", "leaked", Now));

    [Fact]
    public void ARenewalIssuedAfterTheRevoke_IsRefusedToo()
    {
        // Revocation covers the enrollment, past and future: a serial recorded in it later is never accepted.
        var ledger = Ledger(Record("01"));
        ledger.RevokeEnrollment(First, "leaked", Now);

        ledger.RecordIssue(Record("02"), Now);

        Assert.False(ledger.IsAccepted("02"));
    }

    [Fact]
    public void MarkSuperseded_NamesTheReplacementAndStaysAccepted()
    {
        var ledger = Ledger(Record("01"));
        ledger.RecordIssue(Record("02"), Now);

        ledger.MarkSuperseded("01", "0:2");

        var old = ledger.Find("01")!;
        Assert.Equal("02", old.SupersededBy);
        Assert.False(old.Revoked);
        Assert.True(ledger.IsAccepted("01"));
    }

    [Fact]
    public void MarkSuperseded_DoesNothingForAnUnknownSerial()
    {
        var ledger = Ledger(Record("01"));

        ledger.MarkSuperseded("99", "02");

        Assert.Null(ledger.Find("01")!.SupersededBy);
        Assert.Single(ledger.Records);
    }

    [Fact]
    public void RevokingTheOriginal_RevokesItsRenewal()
    {
        var ledger = Ledger(Record("01"));
        ledger.RecordIssue(Record("02"), Now);
        ledger.MarkSuperseded("01", "02");

        ledger.Revoke("01", "leaked", Now);

        Assert.False(ledger.IsAccepted("01"));
        Assert.False(ledger.IsAccepted("02"));
    }

    [Fact]
    public void RevokeAllExcept_RevokesEveryEnrollmentOfAnotherAuthority()
    {
        var ledger = new IssuedCertificateLedger(
            [Record("01"), Record("02", enrollment: Second), Record("03", enrollment: null)],
            [Enrollment(First, "OLD"), Enrollment(Second, "NEW")]);

        Assert.Equal(1, ledger.RevokeAllExcept("new", "hostname changed", Now));

        Assert.False(ledger.IsAccepted("01"));
        Assert.Equal("hostname changed", ledger.FindEnrollment(First)!.RevokedReason);
        Assert.True(ledger.IsAccepted("02"));
        Assert.True(ledger.Find("03")!.Revoked);
    }

    [Fact]
    public void CertificatesOf_ListsAnEnrollmentsSerialsOldestFirst()
    {
        var ledger = Ledger(Record("02") with { IssuedAt = Now.AddDays(1) }, Record("01"), Record("03", enrollment: Second));

        Assert.Equal(["01", "02"], ledger.CertificatesOf(First).Select(record => record.Serial));
    }

    [Fact]
    public void RecordIssue_DropsRecordsExpiredMoreThanAYearAgo()
    {
        var ledger = Ledger(
            Record("01", Now.AddDays(-366)),
            Record("02", Now.AddDays(-365)),
            Record("03", Now.AddDays(-10)));

        ledger.RecordIssue(Record("04"), Now);

        Assert.Equal(["02", "03", "04"], ledger.Records.Select(record => record.Serial));
    }

    [Fact]
    public void RecordIssue_DropsAnEnrollmentWithItsLastRecord()
    {
        // After retention a revoked enrollment is forgotten; its certificates have long expired by then.
        var ledger = Ledger(Record("01", Now.AddDays(-400), enrollment: Second) with { Revoked = true });

        ledger.RecordIssue(Record("02"), Now);

        Assert.Null(ledger.Find("01"));
        Assert.Null(ledger.FindEnrollment(Second));
        Assert.NotNull(ledger.FindEnrollment(First));
    }
}
