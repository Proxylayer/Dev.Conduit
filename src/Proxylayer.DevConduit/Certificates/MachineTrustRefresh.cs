using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>What became of the machine identity in a <see cref="MachineTrustRefresh"/>.</summary>
public enum MachineIdentityRenewal
{
    /// <summary>It has more than sixty days left, or there was nothing to refresh; it was left alone.</summary>
    NotDue,

    /// <summary>It was renewed, and the new one replaced it in the machine layer.</summary>
    Renewed,

    /// <summary>It was due, but the renewal was refused or failed; the current one was kept.</summary>
    Failed
}

/// <summary>What <see cref="MachineTrustRefresh"/> did.</summary>
/// <param name="Found">False when the server was never imported here with a client identity and an endpoint; nothing was changed.</param>
/// <param name="Summary">Lines for the person who ran it.</param>
/// <param name="Identity">Whether the machine identity was renewed.</param>
public sealed record MachineTrustRefreshResult(
    bool Found,
    IReadOnlyList<string> Summary,
    MachineIdentityRenewal Identity = MachineIdentityRenewal.NotDue);

/// <summary>
/// <c>service-admin trust &lt;hostname&gt;</c>, elevated: brings the machine layer's pins and
/// <c>LocalMachine\Root</c> up to date from a source an administrator can trust. It never reads
/// a user's layer, which that user controls. Instead it calls the server's <c>GET /cert</c>
/// itself, authenticated with the machine identity and accepting the server only by Windows
/// trust or the machine pins, takes every published certificate that passes
/// <see cref="ServerCertificateRules"/> for the host, adds the new ones to the machine pins and
/// to Windows trust, and removes the expired ones this app added. The port comes from the
/// remote in the machine settings file; the host name is the argument itself. Then, when the
/// machine identity has less than sixty days left, it renews it through <c>POST /cert/renew</c>,
/// again as the machine identity, and replaces it in the machine layer under machine-scope
/// DPAPI. A refused or failed renewal keeps the current identity and is reported in the
/// result; the pins and Windows trust stay updated. A machine folder an earlier build left
/// with a broader access list gets the machine layer's own first.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MachineTrustRefresh
{
    /// <summary>Calls <c>GET /cert</c> at <paramref name="baseAddress"/> as <paramref name="identity"/>, accepting the server by <paramref name="policy"/>.</summary>
    public delegate Task<CertResponse> CertFetch(Uri baseAddress, X509Certificate2 identity, RemoteCertificatePolicy policy, CancellationToken cancellationToken);

    /// <summary>Calls <c>POST /cert/renew</c> at <paramref name="baseAddress"/> with <paramref name="signingRequest"/> as <paramref name="identity"/>, accepting the server by <paramref name="policy"/>.</summary>
    public delegate Task<CertRenewResponse> CertRenew(
        Uri baseAddress, X509Certificate2 identity, RemoteCertificatePolicy policy, byte[] signingRequest, CancellationToken cancellationToken);

    private readonly ProxyConfigImportTargets _targets;
    private readonly TimeProvider _clock;
    private readonly CertFetch _fetch;
    private readonly CertRenew _renew;

    /// <param name="fetch">How <c>/cert</c> is called; <see cref="CertApiClient"/> unless a test stands in for the server.</param>
    /// <param name="renew">How <c>/cert/renew</c> is called; <see cref="CertApiClient"/> unless a test stands in for the server.</param>
    public MachineTrustRefresh(ProxyConfigImportTargets targets, TimeProvider clock, CertFetch? fetch = null, CertRenew? renew = null)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _fetch = fetch ?? FetchAsync;
        _renew = renew ?? RenewAsync;
    }

    /// <exception cref="ArgumentException"><paramref name="hostname"/> is not a DNS host name.</exception>
    /// <exception cref="HttpRequestException">The server could not be reached or refused the machine identity; nothing was changed.</exception>
    /// <exception cref="InvalidOperationException">The server answered for another host, or Windows trust could not be updated.</exception>
    public async Task<MachineTrustRefreshResult> RefreshAsync(string hostname, CancellationToken cancellationToken)
    {
        hostname = DnsHostname.Normalize(hostname);
        if (!DnsHostname.IsValid(hostname))
        {
            throw new ArgumentException($"'{hostname}' is not a DNS host name.", nameof(hostname));
        }

        var layers = ClientTrustLayers.MachineOnly(_targets.ClientDirectory);
        var folder = layers.MachineFolder(hostname);
        var summary = new List<string>();
        if (folder.Tighten())
        {
            summary.Add($"Limited {folder.Directory} to Administrators and SYSTEM (full control) and Users (read).");
        }

        var endpoint = Endpoint(hostname);
        using var identity = folder.ReadIdentity();
        if (identity is null || endpoint is null)
        {
            summary.Add($"{hostname} was not imported here with a client certificate and a remote, so there is nothing to refresh.");
            return new MachineTrustRefreshResult(false, summary);
        }

        var policy = new RemoteCertificatePolicy(new ClientTrustStore(layers), _clock);
        var answer = await _fetch(endpoint, identity, policy, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(DnsHostname.Normalize(answer.Hostname), hostname, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The server answered as {answer.Hostname}, not {hostname}; nothing was changed.");
        }

        var now = _clock.GetUtcNow();
        var problems = new List<string>();
        var offered = PinnedTrust.Acceptable(answer.ServerCertificates, hostname, now, problems);
        summary.AddRange(problems.Select(problem => $"Not trusted: {problem}"));

        var before = folder.ReadTrust();
        var pins = PinnedTrust.Merge(before, offered, now);
        if (!pins.SequenceEqual(before))
        {
            folder.WriteTrust(pins);
        }

        summary.Add($"Pinned {pins.Count} server certificate{(pins.Count == 1 ? string.Empty : "s")} in {folder.TrustPath}.");

        var trust = new RootStoreTrust(_targets.ClientDirectory.RootStoreRecordPath, _targets.StoreName, _targets.StoreLocation);
        var loaded = PinnedTrust.Load(pins);
        try
        {
            var change = trust.Synchronize(hostname, loaded, now);
            summary.Add(
                $"{trust.StoreDescription}: {change.Added.Count} added, {change.AlreadyPresent.Count} already present, {change.Removed.Count} expired or replaced removed.");
        }
        finally
        {
            foreach (var certificate in loaded)
            {
                certificate.Dispose();
            }
        }

        var renewal = await RenewIdentityAsync(endpoint, identity, policy, folder, summary, cancellationToken).ConfigureAwait(false);
        return new MachineTrustRefreshResult(true, summary, renewal);
    }

    /// <summary>
    /// Renews the machine identity when it is due: a new P-256 key, a signing request with the
    /// same subject, <c>/cert/renew</c> called as the current identity, and the answer checked
    /// and stored in the machine layer in one step. Any refusal or failure keeps the current identity.
    /// </summary>
    private async Task<MachineIdentityRenewal> RenewIdentityAsync(
        Uri endpoint,
        X509Certificate2 identity,
        RemoteCertificatePolicy policy,
        ClientTrustFolder folder,
        List<string> summary,
        CancellationToken cancellationToken)
    {
        var notAfter = new DateTimeOffset(identity.NotAfter.ToUniversalTime());
        if (!ClientCertificateRenewal.IsDue(notAfter, _clock.GetUtcNow()))
        {
            summary.Add(
                $"Machine client certificate {identity.SerialNumber} is valid until {notAfter:yyyy-MM-dd}; it renews from {ClientCertificateRenewal.RenewAfter(notAfter):yyyy-MM-dd}.");
            return MachineIdentityRenewal.NotDue;
        }

        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var answer = await _renew(endpoint, identity, policy, CertificateFactory.CreateSigningRequest(identity.SubjectName, key), cancellationToken)
                .ConfigureAwait(false);

            using var issued = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(answer.Certificate));
            if (!issued.SubjectName.RawData.AsSpan().SequenceEqual(identity.SubjectName.RawData)
                || !CertificateFactory.HasUsage(issued, CertificateFactory.ClientAuthOid)
                || issued.NotAfter.ToUniversalTime() <= identity.NotAfter.ToUniversalTime())
            {
                throw new InvalidOperationException("The server's renewed client certificate does not match the request.");
            }

            // CopyWithPrivateKey refuses a certificate for another key, so only our own request's answer is stored.
            using var withKey = issued.CopyWithPrivateKey(key);
            folder.WriteIdentity(CertificateFactory.ExportPkcs12(withKey));
            summary.Add(
                $"Machine client certificate renewed as {withKey.SerialNumber}, valid until {withKey.NotAfter.ToUniversalTime():yyyy-MM-dd}, in {folder.IdentityPath}.");
            return MachineIdentityRenewal.Renewed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or CryptographicException
                                              or IOException or UnauthorizedAccessException or FormatException or ArgumentException
                                              or System.Text.Json.JsonException or OperationCanceledException)
        {
            summary.Add(
                $"Machine client certificate not renewed: {exception.Message} The current one, {identity.SerialNumber}, valid until {notAfter:yyyy-MM-dd}, is kept.");
            return MachineIdentityRenewal.Failed;
        }
    }

    /// <summary>
    /// <c>https://&lt;hostname&gt;:&lt;port&gt;/</c>, with the port of the https remote in the machine
    /// settings file that names the host, or null when there is none. Only the port is taken
    /// from the file; the host is always the one asked for.
    /// </summary>
    private Uri? Endpoint(string hostname)
    {
        var settings = new SettingsFileWriter(_targets.MachineSettingsPath).Read();
        var remotes = ProxyConfigSettingsMerge.Section(ProxyConfigSettingsMerge.Section(settings, "DevConduit"), "Remotes");
        foreach (var (_, remote) in remotes ?? [])
        {
            var text = remote is JsonObject entry
                ? ProxyConfigSettingsMerge.Text(entry[ProxyConfigSettingsMerge.Find(entry, "Endpoint") ?? "Endpoint"])
                : ProxyConfigSettingsMerge.Text(remote);
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && string.Equals(DnsHostname.Normalize(uri.Host), hostname, StringComparison.Ordinal))
            {
                return new Uri($"https://{hostname}:{uri.Port}/");
            }
        }

        return null;
    }

    private static async Task<CertResponse> FetchAsync(Uri baseAddress, X509Certificate2 identity, RemoteCertificatePolicy policy, CancellationToken cancellationToken)
    {
        using var client = new CertApiClient(baseAddress, identity, policy);
        return await client.GetAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CertRenewResponse> RenewAsync(
        Uri baseAddress, X509Certificate2 identity, RemoteCertificatePolicy policy, byte[] signingRequest, CancellationToken cancellationToken)
    {
        using var client = new CertApiClient(baseAddress, identity, policy);
        return await client.RenewAsync(signingRequest, cancellationToken).ConfigureAwait(false);
    }
}
