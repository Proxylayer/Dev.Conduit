using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Services.Local;

/// <summary>
/// The <c>HTTP_PROXY</c> ingress: a raw TCP listener rather than a Kestrel endpoint, because
/// a forward proxy has to accept the absolute-form request target and the <c>CONNECT</c>
/// verb, neither of which Kestrel will hand to an application. Loopback only, and it never
/// runs in server mode.
/// </summary>
public sealed class ForwardProxyServer : IHostedService
{
    private readonly ForwardProxyConnectionHandler _handler;
    private readonly LocalModeOptions _options;
    private readonly ILogger<ForwardProxyServer> _logger;
    private readonly CancellationTokenSource _stopping = new();

    private TcpListener? _listener;
    private Task? _acceptLoop;

    public ForwardProxyServer(
        ForwardProxyConnectionHandler handler,
        IOptions<DevConduitOptions> options,
        ILogger<ForwardProxyServer> logger)
    {
        _handler = handler;
        _options = options.Value.Local;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.TryResolveBindAddress(out var address, out var error))
        {
            // Startup validation rejects this before the host is built; refusing again here
            // means no code path can bring the listener up off-loopback.
            throw new InvalidOperationException(error);
        }

        _listener = new TcpListener(address, _options.ForwardProxyPort);
        _listener.Start();

        _logger.LogInformation(
            "Forward proxy listening on http://{Address}:{Port}. Point HTTP_PROXY at it.",
            address,
            _options.ForwardProxyPort);

        _acceptLoop = AcceptAsync(_listener, _stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();

        if (_acceptLoop is { } loop)
        {
            try
            {
                await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
                _logger.LogDebug("The forward proxy accept loop did not finish before shutdown completed.");
            }
        }

        _stopping.Dispose();
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException exception)
            {
                _logger.LogWarning(exception, "The forward proxy stopped accepting connections.");
                return;
            }

            // Each connection runs on its own; one slow peer must not stall the accept loop.
            _ = ServeAsync(client, cancellationToken);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            client.NoDelay = true;
            await _handler.HandleAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException
                                          or OperationCanceledException)
        {
            _logger.LogDebug(exception, "A forward-proxy connection ended early.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A forward-proxy connection failed unexpectedly.");
        }
        finally
        {
            client.Dispose();
        }
    }
}
