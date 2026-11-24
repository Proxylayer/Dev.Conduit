using Topshelf;

namespace Proxylayer.DevConduit.Hosting;

/// <summary>
/// The web application as the service host sees it: built when the service is constructed,
/// started and stopped with the service, disposed once stopped.
/// </summary>
public sealed class DevConduitService
{
    private readonly WebApplication _app;
    private readonly ILogger _logger;
    private volatile bool _stopRequestedByHost;

    public DevConduitService(WebApplication app)
    {
        _app = app;
        _logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Dev.Conduit.Service");
    }

    /// <summary>
    /// Starts the web application. A failure, such as a port already in use or a missing
    /// certificate, is logged here, because the service host's own log goes nowhere useful,
    /// and reported as a failed start.
    /// </summary>
    public bool Start(HostControl hostControl)
    {
        try
        {
            _app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The service could not start.");
            return false;
        }

        // When the application shuts itself down, for example after a background service
        // fails, the service must stop too rather than linger as a running service that
        // answers nothing.
        _app.Lifetime.ApplicationStopping.Register(() =>
        {
            if (!_stopRequestedByHost)
            {
                hostControl.Stop(TopshelfExitCode.AbnormalExit);
            }
        });

        return true;
    }

    public bool Stop()
    {
        _stopRequestedByHost = true;

        try
        {
            _app.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The service did not stop cleanly.");
        }
        finally
        {
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return true;
    }
}
