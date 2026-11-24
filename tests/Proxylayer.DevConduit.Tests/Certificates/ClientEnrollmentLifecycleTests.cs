using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>
/// Enrollments on the server's authority, on a throwaway data root: issuing one per export,
/// renewing within an enrollment only inside the renewal window, revoking a whole enrollment,
/// and revoking them all when the host name changes. No Windows certificate store is touched.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClientEnrollmentLifecycleTests : IDisposable
{
    private const string Host = "devconduit.example.internal";

    private readonly TempDirectory _dataRoot = new();
    private readonly ManualClock _clock = new(TestCertificates.Now);
    private readonly ServerCertificateStore _store;
    private readonly ServerCertificateAuthority _authority;
    private readonly List<IDisposable> _disposables = [];

    public ClientEnrollmentLifecycleTests()
    {
        _store = ServerCertificateStore.ForDataRoot(_dataRoot.Path);
        _authority = new ServerCertificateAuthority(_store, _clock, NullLogger.Instance);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _dataRoot.Dispose();
    }

    private ServerCertificateSet Converge(string hostname = Host)
    {
        var set = _authority.Converge(hostname);
        _disposables.Add(set.Current);
        _disposables.Add(set.ClientAuthority);
        if (set.Next is not null)
        {
            _disposables.Add(set.Next);
        }

        return set;
    }

    private IssuedClientCertificate Issue()
    {
        var issued = _authority.IssueClientCertificate(Host);
        _disposables.Add(issued.Certificate);
        return issued;
    }

    // ---- Issue, renew, revoke ------------------------------------------------------------

    [Fact]
    public void Issue_BeforeConverge_IsRefused()
    {
        var started = DateTime.UtcNow;

        var refusal = Assert.Throws<InvalidOperationException>(() => _authority.IssueClientCertificate(Host));

        Assert.Contains("is not prepared", refusal.Message);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "A missing store is reported at once, not after the lock's wait.");
        Assert.False(Directory.Exists(_store.Directory));
    }

    [Fact]
    public void Lock_OfAStoreThatWasNeverPrepared_FailsAtOnce()
    {
        var started = DateTime.UtcNow;

        var refusal = Assert.Throws<InvalidOperationException>(() => _store.Lock());

        Assert.Contains("certificate store", refusal.Message);
        Assert.Contains("not prepared", refusal.Message);
        Assert.IsType<DirectoryNotFoundException>(refusal.InnerException);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Issue_BeforeConvergeWithThePreparedFolder_SaysThereIsNoAuthority()
    {
        _store.Prepare();

        var refusal = Assert.Throws<InvalidOperationException>(() => _authority.IssueClientCertificate(Host));

        Assert.Contains("has no client certificate authority yet", refusal.Message);
    }

    [Fact]
    public void Issue_RecordsTheCertificateInIssuedJson()
    {
        var set = Converge();

        var issued = Issue();

        Assert.True(File.Exists(_store.IssuedPath));
        var record = Assert.Single(_store.ReadIssued().Records);
        Assert.Equal(issued.Certificate.SerialNumber, record.Serial);
        Assert.Equal(issued.Record, record);
        Assert.Equal(issued.Certificate.Subject, record.Subject);
        Assert.Equal(TestCertificates.Now, record.IssuedAt);
        Assert.False(record.Revoked);
        Assert.Null(record.SupersededBy);
        Assert.True(issued.Certificate.HasPrivateKey);
        Assert.True(ClientCertificateValidator.IsAccepted(issued.Certificate, set.ClientAuthority, _authority.ReadIssued(), _clock.Now));
    }

    [Fact]
    public void Issue_CreatesAnEnrollmentNamedInTheLedgerAndInTheCertificate()
    {
        var set = Converge();

        var issued = Issue();

        var enrollment = Assert.Single(_store.ReadIssued().Enrollments);
        Assert.True(ClientEnrollmentId.IsValid(enrollment.Id));
        Assert.Equal(enrollment.Id, issued.Record.Enrollment);
        Assert.Equal(enrollment.Id, ClientEnrollmentId.Of(issued.Certificate.SubjectName));
        Assert.Equal(issued.Certificate.Subject, enrollment.Subject);
        Assert.Equal(Host, enrollment.Hostname);
        Assert.Equal(set.ClientAuthority.Thumbprint, enrollment.Authority);
        Assert.Equal(TestCertificates.Now, enrollment.CreatedAt);
        Assert.False(enrollment.Revoked);

        var text = File.ReadAllText(_store.IssuedPath);
        Assert.Contains("\"enrollments\"", text);
        Assert.Contains("\"certificates\"", text);
    }

    [Fact]
    public void Issue_TwiceCreatesTwoEnrollments()
    {
        Converge();

        var first = Issue();
        var second = Issue();

        Assert.NotEqual(first.Record.Serial, second.Record.Serial);
        Assert.NotEqual(first.Record.Enrollment, second.Record.Enrollment);
        Assert.Equal(2, _authority.ReadIssued().Records.Count);
        Assert.Equal(2, _authority.ReadIssued().Enrollments.Count);
    }

    [Fact]
    public void ReadIssued_OfAListFromBeforeEnrollments_ReadsCertificatesThatAreNotAccepted()
    {
        _store.Prepare();
        File.WriteAllText(
            _store.IssuedPath,
            """[ { "serial": "0A1B", "subject": "CN=old", "notAfter": "2027-01-01T00:00:00Z", "issuedAt": "2026-01-01T00:00:00Z", "revoked": false } ]""");

        var ledger = _store.ReadIssued();

        Assert.Equal("0A1B", Assert.Single(ledger.Records).Serial);
        Assert.Empty(ledger.Enrollments);
        Assert.False(ledger.IsAccepted("0A1B"));
    }

    private (IssuedClientCertificate Issued, ECDsa Key) Renewal(IssuedClientCertificate presented)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _disposables.Add(key);
        var csr = CertificateFactory.CreateSigningRequest(presented.Certificate.SubjectName, key);
        var issued = _authority.Renew(csr, presented.Certificate);
        _disposables.Add(issued.Certificate);
        return (issued, key);
    }

    /// <summary>Moves the clock to <paramref name="daysLeft"/> days before <paramref name="issued"/> expires.</summary>
    private void DaysLeft(IssuedClientCertificate issued, double daysLeft) =>
        _clock.Now = new DateTimeOffset(issued.Certificate.NotAfter.ToUniversalTime()) - TimeSpan.FromDays(daysLeft);

    [Fact]
    public void Renew_IssuesForTheRequestsKeyInTheSameEnrollmentAndSupersedesTheOld()
    {
        var set = Converge();
        var original = Issue();
        DaysLeft(original, 55);

        var (renewed, key) = Renewal(original);

        using var renewedKey = renewed.Certificate.GetECDsaPublicKey()!;
        Assert.Equal(key.ExportSubjectPublicKeyInfo(), renewedKey.ExportSubjectPublicKeyInfo());
        Assert.Equal(original.Certificate.Subject, renewed.Certificate.Subject);
        Assert.False(renewed.Certificate.HasPrivateKey);
        Assert.Equal(original.Record.Enrollment, renewed.Record.Enrollment);

        var ledger = _authority.ReadIssued();
        Assert.Equal(2, ledger.Records.Count);
        Assert.Single(ledger.Enrollments);
        Assert.Equal(renewed.Record.Serial, ledger.Find(original.Record.Serial)!.SupersededBy);
        Assert.Equal(_clock.Now, ledger.Find(renewed.Record.Serial)!.IssuedAt);

        // The superseded certificate stays valid until it expires.
        Assert.True(ClientCertificateValidator.IsAccepted(original.Certificate, set.ClientAuthority, ledger, _clock.Now));
        Assert.True(ClientCertificateValidator.IsAccepted(renewed.Certificate, set.ClientAuthority, ledger, _clock.Now));
    }

    [Theory]
    [InlineData(365)]
    [InlineData(120)]
    [InlineData(60.01)]
    public void Renew_OutsideTheRenewalWindow_IsRefusedAsNotDueAndRecordsNothing(double daysLeft)
    {
        Converge();
        var original = Issue();
        DaysLeft(original, daysLeft);
        var before = File.ReadAllText(_store.IssuedPath);

        var refusal = Assert.Throws<CertificateRenewalException>(() => Renewal(original));

        Assert.Equal(CertificateRenewalRefusal.NotDue, refusal.Refusal);
        Assert.Equal(before, File.ReadAllText(_store.IssuedPath));
    }

    [Fact]
    public void Renew_UsesTheClientsRule_FromSixtyDaysBeforeExpiry()
    {
        Converge();
        var original = Issue();
        var notAfter = new DateTimeOffset(original.Certificate.NotAfter.ToUniversalTime());
        _clock.Now = ClientCertificateRenewal.RenewAfter(notAfter);

        Assert.True(ClientCertificateRenewal.IsDue(notAfter, _clock.Now));
        Renewal(original);
    }

    [Fact]
    public void Renew_ASupersededCertificate_MayRenewAgainWithinItsWindow()
    {
        // Several users of one workstation renew from the same machine identity.
        var set = Converge();
        var original = Issue();
        DaysLeft(original, 50);
        var (first, _) = Renewal(original);

        var (second, _) = Renewal(original);

        var ledger = _authority.ReadIssued();
        Assert.Equal(3, ledger.CertificatesOf(original.Record.Enrollment!).Count);
        Assert.Equal(second.Record.Serial, ledger.Find(original.Record.Serial)!.SupersededBy);
        Assert.True(ClientCertificateValidator.IsAccepted(original.Certificate, set.ClientAuthority, ledger, _clock.Now));
        Assert.True(ClientCertificateValidator.IsAccepted(first.Certificate, set.ClientAuthority, ledger, _clock.Now));
        Assert.True(ClientCertificateValidator.IsAccepted(second.Certificate, set.ClientAuthority, ledger, _clock.Now));
    }

    [Fact]
    public void Renew_RefusesARequestForAnotherSubject()
    {
        Converge();
        var original = Issue();
        DaysLeft(original, 30);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = CertificateFactory.CreateSigningRequest(new X500DistinguishedName("CN=someone else"), key);

        var refusal = Assert.Throws<CertificateRenewalException>(() => _authority.Renew(csr, original.Certificate));

        Assert.Equal(CertificateRenewalRefusal.InvalidRequest, refusal.Refusal);
        Assert.Single(_authority.ReadIssued().Records);
    }

    [Fact]
    public void Renew_RefusesARequestNamingAnotherEnrollment()
    {
        Converge();
        var original = Issue();
        var other = Issue();
        DaysLeft(original, 30);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = CertificateFactory.CreateSigningRequest(other.Certificate.SubjectName, key);

        var refusal = Assert.Throws<CertificateRenewalException>(() => _authority.Renew(csr, original.Certificate));

        Assert.Equal(CertificateRenewalRefusal.InvalidRequest, refusal.Refusal);
        Assert.Equal(2, _authority.ReadIssued().Records.Count);
    }

    [Fact]
    public void Renew_RefusesAnythingButP256()
    {
        Converge();
        var original = Issue();
        DaysLeft(original, 30);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create(2048);
        var rsaCsr = new CertificateRequest(original.Certificate.SubjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequest();

        Assert.Throws<CertificateRenewalException>(() =>
            _authority.Renew(CertificateFactory.CreateSigningRequest(original.Certificate.SubjectName, p384), original.Certificate));
        Assert.Throws<CertificateRenewalException>(() => _authority.Renew(rsaCsr, original.Certificate));
    }

    [Fact]
    public void Renew_RefusesBytesThatAreNotARequest()
    {
        Converge();
        var original = Issue();

        Assert.Throws<CertificateRenewalException>(() => _authority.Renew([1, 2, 3], original.Certificate));
    }

    [Fact]
    public void Renew_RefusesAnExpiredCertificate()
    {
        Converge();
        var original = Issue();
        DaysLeft(original, -1);

        var refusal = Assert.Throws<CertificateRenewalException>(() => Renewal(original));

        Assert.Equal(CertificateRenewalRefusal.NotAccepted, refusal.Refusal);
    }

    [Fact]
    public void Revoke_RevokesTheEnrollmentAndValidationRefusesIt()
    {
        var set = Converge();
        var issued = Issue();

        var revocation = _authority.Revoke(issued.Record.Serial.ToLowerInvariant());

        Assert.Equal(issued.Record.Enrollment, revocation!.Enrollment);
        var ledger = _authority.ReadIssued();
        Assert.True(ledger.Find(issued.Record.Serial)!.Revoked);
        var enrollment = ledger.FindEnrollment(issued.Record.Enrollment)!;
        Assert.True(enrollment.Revoked);
        Assert.Equal(ServerCertificateAuthority.AdministratorRevocation, enrollment.RevokedReason);
        Assert.False(ClientCertificateValidator.IsAccepted(issued.Certificate, set.ClientAuthority, ledger, _clock.Now));
    }

    [Fact]
    public void Revoke_OfAnUnknownSerial_IsNullAndWritesNothing()
    {
        Converge();
        Issue();
        var before = File.ReadAllText(_store.IssuedPath);

        Assert.Null(_authority.Revoke("0123456789ABCDEF"));
        Assert.Null(_authority.RevokeEnrollment("0123456789abcdef0123456789abcdef"));
        Assert.Equal(before, File.ReadAllText(_store.IssuedPath));
    }

    [Fact]
    public void Revoke_OfTheOriginal_ReachesARenewalMadeBeforeIt()
    {
        var set = Converge();
        var original = Issue();
        DaysLeft(original, 40);
        var (renewed, _) = Renewal(original);

        var revocation = _authority.Revoke(original.Record.Serial);

        Assert.Equal(
            new[] { original.Record.Serial, renewed.Record.Serial }.Order(StringComparer.Ordinal),
            revocation!.Serials.Order(StringComparer.Ordinal));
        var ledger = _authority.ReadIssued();
        Assert.False(ClientCertificateValidator.IsAccepted(original.Certificate, set.ClientAuthority, ledger, _clock.Now));
        Assert.False(ClientCertificateValidator.IsAccepted(renewed.Certificate, set.ClientAuthority, ledger, _clock.Now));
    }

    [Fact]
    public void RevokeEnrollment_ByItsId_ReachesEveryCertificateAndLeavesOtherEnrollments()
    {
        var set = Converge();
        var original = Issue();
        var other = Issue();
        DaysLeft(original, 40);
        var (renewed, _) = Renewal(original);

        var revocation = _authority.RevokeEnrollment(original.Record.Enrollment!.ToUpperInvariant());

        Assert.Equal(original.Record.Enrollment, revocation!.Enrollment);
        var ledger = _authority.ReadIssued();
        Assert.False(ClientCertificateValidator.IsAccepted(original.Certificate, set.ClientAuthority, ledger, _clock.Now));
        Assert.False(ClientCertificateValidator.IsAccepted(renewed.Certificate, set.ClientAuthority, ledger, _clock.Now));
        Assert.True(ClientCertificateValidator.IsAccepted(other.Certificate, set.ClientAuthority, ledger, _clock.Now));
    }

    [Fact]
    public void ARevokedCertificate_CannotRenew()
    {
        Converge();
        var original = Issue();
        DaysLeft(original, 30);
        _authority.Revoke(original.Record.Serial);
        var before = File.ReadAllText(_store.IssuedPath);

        var refusal = Assert.Throws<CertificateRenewalException>(() => Renewal(original));

        Assert.Equal(CertificateRenewalRefusal.NotAccepted, refusal.Refusal);
        Assert.Equal(before, File.ReadAllText(_store.IssuedPath));
    }

    [Fact]
    public void ARenewalOfARevokedEnrollment_CannotRenewEither()
    {
        Converge();
        var original = Issue();
        DaysLeft(original, 30);
        var (renewed, renewedKey) = Renewal(original);
        _authority.Revoke(original.Record.Serial);
        _clock.Now = new DateTimeOffset(renewed.Certificate.NotAfter.ToUniversalTime()) - TimeSpan.FromDays(30);
        using var withKey = renewed.Certificate.CopyWithPrivateKey(renewedKey);

        var refusal = Assert.Throws<CertificateRenewalException>(() =>
            _authority.Renew(CertificateFactory.CreateSigningRequest(withKey.SubjectName, renewedKey), withKey));

        Assert.Equal(CertificateRenewalRefusal.NotAccepted, refusal.Refusal);
    }

    // ---- A host name change ------------------------------------------------------------

    [Fact]
    public void Converge_ForANewHostname_RevokesEveryEnrollmentOfTheOldAuthority()
    {
        var first = Converge();
        var original = Issue();
        _clock.Advance(TimeSpan.FromDays(310));
        var (renewed, _) = Renewal(original);

        var renamed = Converge("new-host.devconduit.internal");

        var ledger = _authority.ReadIssued();
        var enrollment = ledger.FindEnrollment(original.Record.Enrollment)!;
        Assert.True(enrollment.Revoked);
        Assert.Equal(ServerCertificateAuthority.HostnameChangedRevocation, enrollment.RevokedReason);
        Assert.Equal(first.ClientAuthority.Thumbprint, enrollment.Authority);
        Assert.Equal(Host, enrollment.Hostname);
        Assert.True(ledger.Find(original.Record.Serial)!.Revoked);
        Assert.True(ledger.Find(renewed.Record.Serial)!.Revoked);

        // Refused by the new authority twice over: another signer, and revoked.
        Assert.False(ClientCertificateValidator.IsAccepted(original.Certificate, renamed.ClientAuthority, ledger, _clock.Now));
        Assert.False(ClientCertificateValidator.IsAccepted(renewed.Certificate, first.ClientAuthority, ledger, _clock.Now));

        // A new export under the new name is accepted.
        var fresh = _authority.IssueClientCertificate("new-host.devconduit.internal");
        _disposables.Add(fresh.Certificate);
        Assert.Equal(renamed.ClientAuthority.Thumbprint, _authority.ReadIssued().FindEnrollment(fresh.Record.Enrollment)!.Authority);
        Assert.True(ClientCertificateValidator.IsAccepted(fresh.Certificate, renamed.ClientAuthority, _authority.ReadIssued(), _clock.Now));
    }
}
