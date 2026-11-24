using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>
/// Keeps the server's app-managed certificate current while the server runs: converges the
/// store on start and every twelve hours, and hands Kestrel the current certificate on every
/// TLS handshake, so a rotation takes effect without a restart. Every convergence also brings
/// Windows trust up to date with the certificates in use (<see cref="ServerSelfTrust"/>).
/// Registered only in server mode with a host name, configured or the computer's, and the
/// managed HTTPS endpoint in use.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServerCertificateManager : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

    private readonly ServerCertificateAuthority _authority;
    private readonly ServerSelfTrust _selfTrust;
    private readonly string _hostname;
    private readonly TimeProvider _clock;
    private readonly ILogger<ServerCertificateManager> _logger;
    private readonly Lock _gate = new();

    private volatile ServerCertificateSet? _set;
    private DateTime _indexWriteTime;

    public ServerCertificateManager(
        ServerCertificateAuthority authority,
        ServerSelfTrust selfTrust,
        IOptions<DevConduitOptions> options,
        TimeProvider clock,
        ILogger<ServerCertificateManager> logger)
    {
        _authority = authority;
        _selfTrust = selfTrust;
        _hostname = DnsHostname.Normalize(options.Value.Server.Hostname);
        _clock = clock;
        _logger = logger;
    }

    public string Hostname => _hostname;

    public ServerCertificateAuthority Authority => _authority;

    /// <summary>
    /// The certificates in use, converging the store first if this is the first call. A
    /// handshake can arrive before the hosted service has started, so the first caller loads.
    /// The index is re-read when another process (an export) changed it.
    /// </summary>
    public ServerCertificateSet Certificates
    {
        get
        {
            var set = _set;
            if (set is not null && !IndexChanged())
            {
                return set;
            }

            lock (_gate)
            {
                if (_set is null)
                {
                    Refresh();
                }
                else if (IndexChanged())
                {
                    try
                    {
                        Refresh();
                    }
                    catch (Exception exception) when (IsStoreFailure(exception))
                    {
                        // Keep serving what works; the scheduled check tries again.
                        _indexWriteTime = File.GetLastWriteTimeUtc(_authority.Store.IndexPath);
                        _logger.LogWarning(exception, "The certificate store changed but could not be reloaded; the certificate in use is kept.");
                    }
                }

                return _set!;
            }
        }
    }

    /// <summary>Kestrel's certificate selector: the current certificate, or null when it could not be loaded.</summary>
    public X509Certificate2? SelectCertificate()
    {
        try
        {
            return Certificates.Current;
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            _logger.LogError(exception, "The app-managed server certificate could not be loaded, so HTTPS cannot be served.");
            return null;
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            lock (_gate)
            {
                Refresh();
            }
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            _logger.LogError(exception, "The app-managed server certificate could not be prepared under {Directory}.", _authority.Store.Directory);
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                lock (_gate)
                {
                    Refresh();
                }
            }
            catch (Exception exception) when (IsStoreFailure(exception))
            {
                _logger.LogError(exception, "The scheduled server certificate check failed; the certificate in use is kept.");
            }
        }
    }

    /// <summary>Converges the store and swaps in the result. Callers hold the gate.</summary>
    private void Refresh()
    {
        var loaded = _authority.Converge(_hostname);
        _indexWriteTime = File.GetLastWriteTimeUtc(_authority.Store.IndexPath);
        _selfTrust.Synchronize(loaded, _clock.GetUtcNow());

        var previous = _set;
        if (previous is not null && SameCertificates(previous, loaded))
        {
            // Keep the objects Kestrel is already serving; the fresh copies are not needed.
            loaded.Current.Dispose();
            loaded.Next?.Dispose();
            loaded.ClientAuthority.Dispose();
            return;
        }

        _set = loaded;
        _logger.LogInformation(
            "Serving the app-managed certificate {Thumbprint} for {Hostname}, valid until {NotAfter:yyyy-MM-dd}{Next}.",
            loaded.Current.Thumbprint,
            _hostname,
            loaded.Current.NotAfter.ToUniversalTime(),
            loaded.Next is null ? string.Empty : $"; next certificate {loaded.Next.Thumbprint} published");

        // The previous objects are not disposed: a handshake may still be using them.
    }

    private bool IndexChanged()
    {
        try
        {
            return File.GetLastWriteTimeUtc(_authority.Store.IndexPath) != _indexWriteTime;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool SameCertificates(ServerCertificateSet left, ServerCertificateSet right) =>
        left.Current.Thumbprint == right.Current.Thumbprint
        && left.Next?.Thumbprint == right.Next?.Thumbprint
        && left.ClientAuthority.Thumbprint == right.ClientAuthority.Thumbprint;

    private static bool IsStoreFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or TimeoutException
            or System.Security.Cryptography.CryptographicException or InvalidDataException
            or System.Text.Json.JsonException or InvalidOperationException;
}
