using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>An issued client certificate with its private key, and its ledger record.</summary>
public sealed record IssuedClientCertificate(X509Certificate2 Certificate, IssuedCertificateRecord Record);

/// <summary>The kind of refusal a renewal met, which decides the bare status code <c>/cert/renew</c> answers.</summary>
public enum CertificateRenewalRefusal
{
    /// <summary>The request is not one the authority signs: not PKCS#10, not P-256, or not the enrollment's subject. 400.</summary>
    InvalidRequest,

    /// <summary>The presented certificate is not accepted: unknown, revoked, expired or from another authority. 403.</summary>
    NotAccepted,

    /// <summary>The presented certificate has more than the renewal window left. 409.</summary>
    NotDue
}

/// <summary>Why a renewal request was refused. The caller answers with a bare status code.</summary>
public sealed class CertificateRenewalException(string message, CertificateRenewalRefusal refusal = CertificateRenewalRefusal.InvalidRequest)
    : Exception(message)
{
    public CertificateRenewalRefusal Refusal { get; } = refusal;
}

/// <summary>
/// Everything the server does with its certificates, on the store, under the store's lock:
/// bringing the server certificate and the client authority up to date for a host name,
/// creating an enrollment and its client certificate for each export, renewing within an
/// enrollment from a signing request, and revoking enrollments. The running service and the
/// elevated export command both use it, so they can never disagree about which certificate
/// is current.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServerCertificateAuthority
{
    public const string AdministratorRevocation = "revoked by an administrator";
    public const string HostnameChangedRevocation = "hostname changed";
    public const string AuthorityExpiredRevocation = "client authority expired";
    public const string UndeliveredRevocation = "the export could not be written";

    private const string ServerKind = "server";
    private const string AuthorityKind = "client-ca";

    private readonly ServerCertificateStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    public ServerCertificateAuthority(ServerCertificateStore store, TimeProvider clock, ILogger logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ServerCertificateStore Store => _store;

    /// <summary>
    /// Creates whatever is missing and carries out every rotation step that is due for
    /// <paramref name="hostname"/>, then loads the result.
    /// </summary>
    public ServerCertificateSet Converge(string hostname)
    {
        hostname = DnsHostname.Normalize(hostname);
        if (_store.Prepare() is { } warning)
        {
            _logger.LogWarning("{Warning}", warning);
        }

        using (_store.Lock())
        {
            var original = _store.ReadIndex();
            var now = _clock.GetUtcNow();

            var index = EnsureAuthority(original, hostname, now);

            for (var step = 0; step < 3; step++)
            {
                var plan = ServerCertificateRotation.Plan(hostname, index.Current?.Facts, index.Next?.Facts, now);
                if (plan.Step == ServerCertificateStep.None)
                {
                    break;
                }

                index = Apply(index, plan, hostname, now);
            }

            if (index != original)
            {
                _store.WriteIndex(index);
            }

            return Load(index, hostname);
        }
    }

    /// <summary>Creates an enrollment for an export, with a random id, and issues and records its first client certificate.</summary>
    public IssuedClientCertificate IssueClientCertificate(string hostname)
    {
        hostname = DnsHostname.Normalize(hostname);
        using (_store.Lock())
        {
            var now = _clock.GetUtcNow();
            using var authority = LoadAuthority(_store.ReadIndex());
            var enrollment = ClientEnrollmentId.New();
            var subject = CertificateFactory.ClientSubject(hostname, now, enrollment);
            var certificate = CertificateFactory.CreateClientCertificate(authority, subject, now);
            var record = Record(certificate, now, enrollment);
            var ledger = _store.ReadIssued();
            ledger.Enroll(new ClientEnrollment(enrollment, subject.Name, hostname, authority.Thumbprint, now), record, now);
            _store.WriteIssued(ledger);

            _logger.LogInformation(
                "Created enrollment {Enrollment} and issued its client certificate {Serial} ({Subject}), valid until {NotAfter:yyyy-MM-dd}.",
                enrollment, record.Serial, record.Subject, record.NotAfter);
            return new IssuedClientCertificate(certificate, record);
        }
    }

    /// <summary>
    /// Issues a replacement for <paramref name="presented"/> from a PKCS#10 request: a P-256
    /// key the client generated, under the subject of the presented certificate's enrollment.
    /// The presented certificate must be accepted (issued here, in its validity period, its
    /// enrollment not revoked) and inside its renewal window, by the rule the client uses.
    /// The new certificate joins the same enrollment; the old one is marked superseded and
    /// stays valid until it expires, and may itself renew again within its window, since
    /// several users of one workstation renew from the same machine identity.
    /// </summary>
    /// <exception cref="CertificateRenewalException">The request is not one the authority signs, or the presented certificate may not renew.</exception>
    public IssuedClientCertificate Renew(byte[] signingRequest, X509Certificate2 presented)
    {
        CertificateRequest request;
        try
        {
            request = CertificateRequest.LoadSigningRequest(
                signingRequest, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default);
        }
        catch (CryptographicException)
        {
            throw new CertificateRenewalException("The signing request is not a valid PKCS#10 request.");
        }

        using (var key = request.PublicKey.GetECDsaPublicKey())
        {
            if (key is null || key.KeySize != 256)
            {
                throw new CertificateRenewalException("The signing request's key is not an ECDSA P-256 key.");
            }
        }

        using (_store.Lock())
        {
            var now = _clock.GetUtcNow();
            using var authority = LoadAuthority(_store.ReadIndex());
            var ledger = _store.ReadIssued();

            if (!ClientCertificateValidator.IsAccepted(presented, authority, ledger, now))
            {
                throw new CertificateRenewalException(
                    "The presented certificate is not accepted: unknown, revoked, outside its validity or from another authority.",
                    CertificateRenewalRefusal.NotAccepted);
            }

            var notAfter = new DateTimeOffset(presented.NotAfter.ToUniversalTime());
            if (!ClientCertificateRenewal.IsDue(notAfter, now))
            {
                throw new CertificateRenewalException(
                    $"The presented certificate has more than {ClientCertificateRenewal.RenewWithin.Days} days left; it may renew from {ClientCertificateRenewal.RenewAfter(notAfter):yyyy-MM-dd}.",
                    CertificateRenewalRefusal.NotDue);
            }

            var enrollment = ledger.FindEnrollment(ledger.Find(presented.SerialNumber)!.Enrollment)!;
            if (!request.SubjectName.RawData.AsSpan().SequenceEqual(presented.SubjectName.RawData)
                || ClientEnrollmentId.Of(request.SubjectName) != enrollment.Id
                || !string.Equals(request.SubjectName.Name, enrollment.Subject, StringComparison.Ordinal))
            {
                throw new CertificateRenewalException("The signing request does not name the subject of the presented certificate's enrollment.");
            }

            var certificate = CertificateFactory.IssueClientCertificate(authority, request, now);
            var record = Record(certificate, now, enrollment.Id);

            ledger.RecordIssue(record, now);
            ledger.MarkSuperseded(presented.SerialNumber, record.Serial);
            _store.WriteIssued(ledger);

            _logger.LogInformation(
                "Renewed client certificate {OldSerial} of enrollment {Enrollment} as {Serial}, valid until {NotAfter:yyyy-MM-dd}.",
                presented.SerialNumber, enrollment.Id, record.Serial, record.NotAfter);
            return new IssuedClientCertificate(certificate, record);
        }
    }

    /// <summary>
    /// Revokes the whole enrollment <paramref name="serial"/> belongs to: every certificate in
    /// it, those issued before and any a renewal could issue after. Null when the serial was never issued here.
    /// </summary>
    public EnrollmentRevocation? Revoke(string serial) =>
        Change(ledger => ledger.Revoke(serial, AdministratorRevocation, _clock.GetUtcNow()));

    /// <summary>Revokes an enrollment and every certificate in it. Null when there is no such enrollment.</summary>
    public EnrollmentRevocation? RevokeEnrollment(string enrollment, string reason = AdministratorRevocation) =>
        Change(ledger => ledger.RevokeEnrollment(enrollment, reason, _clock.GetUtcNow()));

    /// <summary>The thumbprint of the client authority in use, or null before the first start.</summary>
    public string? CurrentAuthorityThumbprint()
    {
        using (_store.Lock())
        {
            return _store.ReadIndex().ClientAuthority?.Thumbprint;
        }
    }

    /// <summary>The issued client certificates, read under the lock.</summary>
    public IssuedCertificateLedger ReadIssued()
    {
        using (_store.Lock())
        {
            return _store.ReadIssued();
        }
    }

    private EnrollmentRevocation? Change(Func<IssuedCertificateLedger, EnrollmentRevocation?> change)
    {
        using (_store.Lock())
        {
            var ledger = _store.ReadIssued();
            var revocation = change(ledger);
            if (revocation is not null)
            {
                _store.WriteIssued(ledger);
                _logger.LogInformation(
                    "Revoked enrollment {Enrollment}: {Serials}.", revocation.Enrollment ?? "(none)", string.Join(", ", revocation.Serials));
            }

            return revocation;
        }
    }

    /// <summary>
    /// Keeps the client authority while it is valid and was created for <paramref name="hostname"/>.
    /// Otherwise a new one replaces it, every enrollment the old one signed is revoked, and the
    /// old one's key is deleted at once: <c>certs list</c> reports those enrollments from the
    /// ledger alone, which keeps the old authority's thumbprint and host name until they are dropped.
    /// </summary>
    private ServerCertificateIndex EnsureAuthority(ServerCertificateIndex index, string hostname, DateTimeOffset now)
    {
        var existing = index.ClientAuthority;
        var hostnameChanged = existing is not null && !string.Equals(existing.Hostname, hostname, StringComparison.OrdinalIgnoreCase);
        if (existing is not null && now < existing.NotAfter && !hostnameChanged)
        {
            return index;
        }

        using var created = CertificateFactory.CreateClientAuthority(hostname, now);
        var stored = Save(AuthorityKind, created, hostname);
        _logger.LogInformation(
            "Created the client certificate authority {Thumbprint} for {Hostname}, valid until {NotAfter:yyyy-MM-dd}. It signs client certificates only and never leaves this machine.",
            stored.Thumbprint, hostname, stored.NotAfter);

        if (existing is null)
        {
            return index with { ClientAuthority = stored };
        }

        var reason = hostnameChanged ? HostnameChangedRevocation : AuthorityExpiredRevocation;
        var ledger = _store.ReadIssued();
        var revoked = ledger.RevokeAllExcept(stored.Thumbprint, reason, now);
        _store.WriteIssued(ledger);

        if (hostnameChanged)
        {
            _logger.LogWarning(
                "The host name changed from {OldHostname} to {Hostname}, so the client certificate authority {OldThumbprint} was replaced by "
                + "{Thumbprint} and its {Count} enrollment(s) were revoked (reason: {Reason}). Every client must import a new .conduit: "
                + "export one for each client.",
                existing.Hostname, hostname, existing.Thumbprint, stored.Thumbprint, revoked, reason);
        }
        else
        {
            _logger.LogWarning(
                "The client certificate authority {OldThumbprint} expired and was replaced by {Thumbprint}; its {Count} enrollment(s) were revoked. "
                + "Every client must import a new .conduit.",
                existing.Thumbprint, stored.Thumbprint, revoked);
        }

        Discard(existing);
        return index with { ClientAuthority = stored };
    }

    private ServerCertificateIndex Apply(ServerCertificateIndex index, ServerCertificatePlan plan, string hostname, DateTimeOffset now)
    {
        switch (plan.Step)
        {
            case ServerCertificateStep.ReplaceCurrent:
            {
                var stored = CreateServer(hostname, now);
                if (plan.HostnameChanged)
                {
                    _logger.LogWarning(
                        "Issued a new server certificate {Thumbprint} because {Reason}. Clients pinned to the old certificate cannot "
                        + "validate the new one: export a new .conduit and import it on every client.",
                        stored.Thumbprint, plan.Reason);
                }
                else if (index.Current is not null)
                {
                    _logger.LogWarning(
                        "Issued a new server certificate {Thumbprint} at once because {Reason}. Clients that have not pinned it must "
                        + "import a new .conduit.",
                        stored.Thumbprint, plan.Reason);
                }
                else
                {
                    _logger.LogInformation(
                        "Issued the server certificate {Thumbprint} for {Hostname}, valid until {NotAfter:yyyy-MM-dd}.",
                        stored.Thumbprint, hostname, stored.NotAfter);
                }

                Discard(index.Current);
                Discard(index.Next);
                return index with { Current = stored, Next = null };
            }

            case ServerCertificateStep.IssueNext:
            {
                var stored = CreateServer(hostname, now);
                _logger.LogInformation(
                    "Issued the next server certificate {Thumbprint}, valid until {NotAfter:yyyy-MM-dd}, because {Reason}. "
                    + "It is published through /cert now and served from {SwitchDate:yyyy-MM-dd}.",
                    stored.Thumbprint, stored.NotAfter, plan.Reason, index.Current!.NotAfter - ServerCertificateRotation.SwitchWithin);
                Discard(index.Next);
                return index with { Next = stored };
            }

            case ServerCertificateStep.PromoteNext:
                _logger.LogInformation(
                    "Switched to the next server certificate {Thumbprint} because {Reason}.",
                    index.Next!.Thumbprint, plan.Reason);
                Discard(index.Current);
                return index with { Current = index.Next, Next = null };

            default:
                return index;
        }
    }

    private StoredCertificate CreateServer(string hostname, DateTimeOffset now)
    {
        using var created = CertificateFactory.CreateServerCertificate(hostname, now);
        return Save(ServerKind, created, hostname);
    }

    private StoredCertificate Save(string kind, X509Certificate2 certificate, string hostname)
    {
        var file = _store.SavePkcs12(kind, certificate.Thumbprint, CertificateFactory.ExportPkcs12(certificate));
        return new StoredCertificate(file, certificate.Thumbprint, hostname, certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime());
    }

    private void Discard(StoredCertificate? stored)
    {
        if (stored is null)
        {
            return;
        }

        try
        {
            _store.Delete(stored);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not delete the retired certificate file {File}: {Message}", stored.File, exception.Message);
        }
    }

    private ServerCertificateSet Load(ServerCertificateIndex index, string hostname)
    {
        var current = CertificateFactory.LoadPkcs12(_store.LoadPkcs12(index.Current!));
        var next = index.Next is null ? null : CertificateFactory.LoadPkcs12(_store.LoadPkcs12(index.Next));
        var authority = LoadAuthority(index);
        return new ServerCertificateSet(hostname, current, next, authority);
    }

    private X509Certificate2 LoadAuthority(ServerCertificateIndex index) =>
        index.ClientAuthority is { } stored
            ? CertificateFactory.LoadPkcs12(_store.LoadPkcs12(stored), ephemeral: true)
            : throw new InvalidOperationException(
                $"The certificate store {_store.Directory} has no client certificate authority yet. Start the server once with DevConduit:Server:Hostname set.");

    private static IssuedCertificateRecord Record(X509Certificate2 certificate, DateTimeOffset now, string enrollment) =>
        new(
            IssuedCertificateLedger.NormalizeSerial(certificate.SerialNumber),
            certificate.Subject,
            certificate.NotAfter.ToUniversalTime(),
            now,
            Enrollment: enrollment);
}
