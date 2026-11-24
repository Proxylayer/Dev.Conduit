using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Configuration;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Proxylayer.DevConduit.Services.Local;

/// <summary>
/// Acquires Dev.Conduit access tokens for the developer at the keyboard: silently from the
/// encrypted on-disk cache when possible, interactively when the cache cannot satisfy
/// the request. Signing in as a person rather than a service principal is the point,
/// because routed history is attributed to whoever the token belongs to.
/// </summary>
/// <remarks>
/// Each distinct tenant and client id pair gets its own MSAL public client, so remotes that
/// share an app registration share one sign-in. Every client is registered with one shared
/// <see cref="MsalCacheHelper"/> over one cache file: MSAL keys cache entries by client id,
/// reloads the whole file before each access and writes it whole afterwards, so tokens for
/// one client survive another client's writes. The helper's file lock excludes other
/// processes but not other threads in this one, which is why every acquisition, for every
/// identity, goes through the same gate.
/// </remarks>
public sealed class MsalDevConduitTokenProvider : IDevConduitTokenProvider, IDisposable
{
    private const string CacheFileName = "devconduit.msal.cache";

    /// <summary>How long before expiry a cached token stops being handed out.</summary>
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LocalModeOptions _options;
    private readonly ILogger<MsalDevConduitTokenProvider> _logger;

    /// <summary>Public clients by <see cref="RemoteIdentity.Key"/>. Touched only under the gate.</summary>
    private readonly Dictionary<string, IPublicClientApplication> _applications = new(StringComparer.Ordinal);

    /// <summary>Tokens by identity and scopes, read without the gate on the fast path.</summary>
    private readonly ConcurrentDictionary<string, CachedToken> _tokens = new(StringComparer.Ordinal);

    private MsalCacheHelper? _cacheHelper;
    private bool _cacheHelperAttempted;

    /// <summary>Creates the provider. No network or disk work happens until a token is asked for.</summary>
    public MsalDevConduitTokenProvider(IOptions<DevConduitOptions> options, ILogger<MsalDevConduitTokenProvider> logger)
    {
        _options = options.Value.Local;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<string> GetAccessTokenAsync(RemoteDefinition remote, CancellationToken cancellationToken)
    {
        var key = TokenKey(remote);
        if (_tokens.TryGetValue(key, out var cached) && cached.IsUsable)
        {
            return cached.Token;
        }

        // One sign-in at a time: concurrent requests must not each open a browser window.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tokens.TryGetValue(key, out var current) && current.IsUsable)
            {
                return current.Token;
            }

            var result = await AcquireAsync(remote, cancellationToken).ConfigureAwait(false);
            _tokens[key] = new CachedToken(result.AccessToken, result.ExpiresOn);
            return result.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private static string TokenKey(RemoteDefinition remote) =>
        remote.Identity.Key + "|" + string.Join(" ", remote.Scopes);

    private async Task<AuthenticationResult> AcquireAsync(RemoteDefinition remote, CancellationToken cancellationToken)
    {
        var application = await GetApplicationAsync(remote).ConfigureAwait(false);
        var accounts = await application.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault();
        var scopes = remote.Scopes.ToArray();

        if (account is not null)
        {
            try
            {
                return await application
                    .AcquireTokenSilent(scopes, account)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (MsalUiRequiredException exception)
            {
                _logger.LogInformation(
                    "The cached Dev.Conduit token for remote '{Remote}' needs a fresh sign-in: {Reason}.",
                    remote.Name,
                    exception.Message);
            }
        }

        return await AcquireWithUserAsync(application, remote, scopes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthenticationResult> AcquireWithUserAsync(
        IPublicClientApplication application,
        RemoteDefinition remote,
        string[] scopes,
        CancellationToken cancellationToken)
    {
        if (_options.InteractiveFallback && Environment.UserInteractive)
        {
            try
            {
                _logger.LogInformation(
                    "Signing in to Dev.Conduit interactively for remote '{Remote}' (client {ClientId}).",
                    remote.Name,
                    remote.Identity.ClientId);
                return await application
                    .AcquireTokenInteractive(scopes)
                    .WithPrompt(Prompt.SelectAccount)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (MsalClientException exception)
            {
                // No browser, no window handle, or the user closed the dialog.
                _logger.LogInformation("Interactive sign-in was unavailable ({Reason}); falling back to device code.", exception.Message);
            }
        }

        return await application
            .AcquireTokenWithDeviceCode(scopes, result =>
            {
                _logger.LogWarning("Dev.Conduit sign-in required for remote '{Remote}': {Message}", remote.Name, result.Message);
                return Task.CompletedTask;
            })
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IPublicClientApplication> GetApplicationAsync(RemoteDefinition remote)
    {
        var identity = remote.Identity;
        if (_applications.TryGetValue(identity.Key, out var existing))
        {
            return existing;
        }

        if (!identity.IsComplete)
        {
            // MSAL would otherwise fail with a bare "Parameter 'tenant'" argument error,
            // which says nothing about which setting is missing or where it lives.
            throw new InvalidOperationException(
                $"No Entra token can be acquired for the remote '{remote.Name}': its tenant and client id "
                + $"({remote.ConfigurationPath}:TenantId and :ClientId, which default to AzureAd:TenantId and "
                + "AzureAd:ClientId) must both name the app registration that remote validates against. "
                + "Set them in appsettings.json and restart.");
        }

        var application = PublicClientApplicationBuilder
            .Create(identity.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, identity.TenantId)
            .WithDefaultRedirectUri()
            .Build();

        if (await GetCacheHelperAsync().ConfigureAwait(false) is { } helper)
        {
            helper.RegisterCache(application.UserTokenCache);
        }

        _applications[identity.Key] = application;
        return application;
    }

    /// <summary>
    /// Opens the encrypted on-disk cache once, so a developer signs in once per machine
    /// rather than once per process. DPAPI protects the file on Windows. An unusable cache
    /// is reported once and every client then keeps its tokens in memory.
    /// </summary>
    private async Task<MsalCacheHelper?> GetCacheHelperAsync()
    {
        if (_cacheHelperAttempted)
        {
            return _cacheHelper;
        }

        _cacheHelperAttempted = true;
        var directory = _options.ResolveCacheDirectory();

        try
        {
            Directory.CreateDirectory(directory);
            var storage = new StorageCreationPropertiesBuilder(CacheFileName, directory).Build();
            _cacheHelper = await MsalCacheHelper.CreateAsync(storage).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is MsalCachePersistenceException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // An unusable cache costs an extra sign-in, not a failed request.
            _logger.LogWarning(
                exception,
                "The Dev.Conduit token cache at {Directory} could not be opened; tokens will live in memory only.",
                directory);
        }

        return _cacheHelper;
    }

    private sealed record CachedToken(string Token, DateTimeOffset ExpiresOn)
    {
        public bool IsUsable => ExpiresOn - DateTimeOffset.UtcNow > RefreshSkew;
    }
}
