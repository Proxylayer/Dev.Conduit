using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Services.Local;

/// <summary>
/// Acquires the first Entra token for each identity while the host is starting rather than
/// on the first proxied call. Deferring it would mean a developer's application appears to
/// hang on its first request while a browser window opens behind it, with nothing on screen
/// to explain the wait. A cached token makes this silent and immediate.
/// </summary>
public sealed class LocalTokenWarmup : IHostedService
{
    private readonly IDevConduitTokenProvider _tokens;
    private readonly RoutingConfiguration _routing;
    private readonly LocalModeOptions _options;
    private readonly ILogger<LocalTokenWarmup> _logger;

    public LocalTokenWarmup(
        IDevConduitTokenProvider tokens,
        RoutingConfiguration routing,
        IOptions<DevConduitOptions> options,
        ILogger<LocalTokenWarmup> logger)
    {
        _tokens = tokens;
        _routing = routing;
        _options = options.Value.Local;
        _logger = logger;
    }

    /// <summary>
    /// Warms one remote per distinct identity, in remote order, so each app registration asks
    /// for at most one sign-in here. Remotes sharing an identity but asking for other scopes
    /// are then served silently from the same account on their first request.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var group in _routing.Remotes.GroupBy(remote => remote.Identity.Key))
        {
            await WarmAsync(group.First(), [.. group.Select(remote => remote.Name)], cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task WarmAsync(RemoteDefinition remote, string[] sharing, CancellationToken cancellationToken)
    {
        var names = string.Join(", ", sharing);

        if (!remote.Identity.IsComplete)
        {
            _logger.LogWarning(
                "No Entra tenant and client id are configured for the remote(s) {Remotes} (AzureAd:TenantId and "
                + "AzureAd:ClientId, or TenantId and ClientId on the remote under DevConduit:Remotes to override them), "
                + "so no token can be acquired for them. Local mode will start and serve the console, but every "
                + "request relayed to them will fail until they are configured.",
                names);
            return;
        }

        _logger.LogInformation(
            "Acquiring a Dev.Conduit access token for the remote(s) {Remotes} ({Identity}) for {Scopes}. If no cached "
            + "token is available a sign-in will start now, before any application traffic arrives.",
            names,
            remote.DescribeIdentity(),
            string.Join(", ", remote.Scopes));

        try
        {
            await _tokens.GetAccessTokenAsync(remote, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "A Dev.Conduit access token is held for {Remotes}. Requests relayed there will not prompt for sign-in.",
                names);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Starting anyway keeps the console reachable and the error visible in one place;
            // each relayed request then reports the same failure with its own context.
            _logger.LogError(
                exception,
                "Sign-in for the remote(s) {Remotes} failed. The host will keep running, but requests relayed there "
                + "will fail until sign-in succeeds. Token cache: {CacheDirectory}",
                names,
                _options.ResolveCacheDirectory());
        }
    }
}
