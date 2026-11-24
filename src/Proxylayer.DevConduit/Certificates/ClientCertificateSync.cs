using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>
/// Keeps each imported server's trust and the client certificate current for the user running
/// local mode (console or tray): on start, every twelve hours, and whenever
/// <see cref="RequestSync"/> is called, for example right after an import. For every remote
/// imported with a client certificate it calls <c>GET /cert</c> with the effective identity
/// (<see cref="ClientTrustLayers"/>), pins any new server certificate the answer carries in the
/// user's own layer and drops expired ones there, renews the client certificate through
/// <c>POST /cert/renew</c> when it has less than sixty days left and stores the renewal in the
/// user's layer with user-scope DPAPI, and notes on <see cref="ClientTrustStatusBoard"/> the
/// effective view, including a pinned certificate missing from Windows trust and a machine
/// identity due for renewal, both of which <c>service-admin trust</c> settles. It never writes
/// the machine layer. Keys are never logged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClientCertificateSync : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    private readonly RoutingConfiguration _routing;
    private readonly ClientTrustStore _trust;
    private readonly RemoteCertificatePolicy _policy;
    private readonly ClientTrustStatusBoard _status;
    private readonly TimeProvider _clock;
    private readonly ILogger<ClientCertificateSync> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);

    public ClientCertificateSync(
        RoutingConfiguration routing,
        ClientTrustStore trust,
        RemoteCertificatePolicy policy,
        ClientTrustStatusBoard status,
        TimeProvider clock,
        ILogger<ClientCertificateSync> logger)
    {
        _routing = routing;
        _trust = trust;
        _policy = policy;
        _status = status;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Runs a sync as soon as the current one, if any, finishes.</summary>
    public void RequestSync()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A sync is already requested.
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting before the first network call.
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            await SyncAllAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(Interval);
                await _wake.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // The interval passed.
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Syncs every imported remote once.</summary>
    public async Task SyncAllAsync(CancellationToken cancellationToken)
    {
        var layers = _trust.Layers;
        foreach (var remote in _routing.Remotes)
        {
            if (remote.BaseAddress is not { Scheme: "https" } address || !DnsHostname.IsValid(DnsHostname.Normalize(address.Host)))
            {
                continue;
            }

            var hostname = DnsHostname.Normalize(address.Host);
            if (!layers.HasIdentity(hostname))
            {
                continue;
            }

            try
            {
                await SyncAsync(remote.Name, address, hostname, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException
                                                  or CryptographicException or System.Text.Json.JsonException or OperationCanceledException
                                                  or InvalidOperationException)
            {
                _logger.LogWarning("Certificate sync with remote '{Remote}' ({Host}) failed: {Message}", remote.Name, address.Host, exception.Message);
                Report(hostname, identityNotAfter: null, synced: null, error: exception.Message);
            }
        }
    }

    private async Task SyncAsync(string remoteName, Uri address, string hostname, CancellationToken cancellationToken)
    {
        var layers = _trust.Layers;
        using var identity = layers.Identity(hostname, _clock.GetUtcNow())
            ?? throw new InvalidOperationException($"The client certificate in {layers.MachineFolder(hostname).IdentityPath} is missing.");

        _logger.LogInformation("Checking certificates with remote '{Remote}' at {Address}.", remoteName, address);
        using var client = new CertApiClient(address, identity, _policy);
        var answer = await client.GetAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();

        if (!string.Equals(DnsHostname.Normalize(answer.Hostname), hostname, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The server answered as {answer.Hostname}, not {hostname}; nothing was pinned.");
        }

        var problems = new List<string>();
        var offered = PinnedTrust.Acceptable(answer.ServerCertificates, hostname, now, problems);
        foreach (var problem in problems)
        {
            _logger.LogWarning("Not pinning a certificate offered by {Host}: {Problem}", hostname, problem);
        }

        if (layers.UserFolder(hostname) is { } userFolder)
        {
            var effectiveBefore = layers.Pins(hostname);
            var before = userFolder.ReadTrust();
            var after = PinnedTrust.Merge(before, offered.Where(pin => effectiveBefore.All(existing => !SameThumbprint(existing, pin))), now);
            var added = after.Count(pin => before.All(existing => !SameThumbprint(existing, pin)));
            var dropped = before.Count(pin => after.All(kept => !SameThumbprint(kept, pin)));
            if (added > 0 || dropped > 0)
            {
                userFolder.Create();
                userFolder.WriteTrust(after);
                _logger.LogInformation(
                    "Remote '{Remote}': pinned {Added} new server certificate(s) for this user, dropped {Dropped} expired; {Count} pinned in all.",
                    remoteName, added, dropped, layers.Pins(hostname).Count);
            }
        }

        DateTimeOffset notAfter = identity.NotAfter.ToUniversalTime();
        if (ClientCertificateRenewal.IsDue(notAfter, now) && layers.UserFolder(hostname) is { } renewalFolder)
        {
            notAfter = await RenewAsync(remoteName, client, identity, renewalFolder, cancellationToken).ConfigureAwait(false);
        }

        Report(hostname, notAfter, now, error: null);
    }

    private async Task<DateTimeOffset> RenewAsync(
        string remoteName,
        CertApiClient client,
        X509Certificate2 identity,
        ClientTrustFolder userFolder,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Remote '{Remote}': the client certificate {Serial} expires on {NotAfter:yyyy-MM-dd}; renewing it.",
            remoteName, identity.SerialNumber, identity.NotAfter.ToUniversalTime());

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var answer = await client
            .RenewAsync(CertificateFactory.CreateSigningRequest(identity.SubjectName, key), cancellationToken)
            .ConfigureAwait(false);

        using var issued = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(answer.Certificate));
        if (!issued.SubjectName.RawData.AsSpan().SequenceEqual(identity.SubjectName.RawData)
            || !CertificateFactory.HasUsage(issued, CertificateFactory.ClientAuthOid))
        {
            throw new InvalidOperationException("The server's renewed client certificate does not match the request; the current one is kept.");
        }

        using var withKey = issued.CopyWithPrivateKey(key);
        userFolder.Create();
        userFolder.WriteIdentity(CertificateFactory.ExportPkcs12(withKey));
        _logger.LogInformation(
            "Remote '{Remote}': stored the renewed client certificate {Serial}, valid until {NotAfter:yyyy-MM-dd}, for this user in {Path}.",
            remoteName, answer.Serial, answer.NotAfter, userFolder.IdentityPath);
        return answer.NotAfter;
    }

    private static bool SameThumbprint(PublishedServerCertificate left, PublishedServerCertificate right) =>
        string.Equals(left.Thumbprint, right.Thumbprint, StringComparison.OrdinalIgnoreCase);

    private void Report(string hostname, DateTimeOffset? identityNotAfter, DateTimeOffset? synced, string? error)
    {
        var pins = PinnedTrust.Load(_trust.Layers.Pins(hostname));
        bool rootUpdateNeeded;
        try
        {
            rootUpdateNeeded = new RootStoreTrust(_trust.Layers.Machine.RootStoreRecordPath).Missing(pins).Count > 0;
        }
        catch (CryptographicException)
        {
            rootUpdateNeeded = false;
        }

        if (rootUpdateNeeded)
        {
            _logger.LogInformation(
                "A pinned certificate for {Host} is not in LocalMachine\\Root yet. The relay does not need it; run "
                + "Dev.Conduit.exe service-admin trust {Host} elevated (or accept the tray's prompt) to add it for other programs.",
                hostname, hostname);
        }

        var machineRenewalDue = _trust.Layers.MachineIdentityRenewalDue(hostname, _clock.GetUtcNow());
        if (machineRenewalDue)
        {
            _logger.LogInformation(
                "This computer's client certificate for {Host} is due for renewal. This user's relay renews its own; run "
                + "Dev.Conduit.exe service-admin trust {Host} elevated (or accept the tray's prompt) to renew the one other users start from.",
                hostname, hostname);
        }

        var previous = _status.Current.FirstOrDefault(status => string.Equals(status.Hostname, hostname, StringComparison.OrdinalIgnoreCase));
        _status.Set(new ClientTrustStatus(
            hostname,
            pins.Count,
            rootUpdateNeeded,
            identityNotAfter ?? previous?.ClientCertificateNotAfter,
            synced ?? previous?.LastSynced,
            error,
            machineRenewalDue));

        foreach (var pin in pins)
        {
            pin.Dispose();
        }
    }
}
