using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Hosting;

namespace Proxylayer.DevConduit.Tray;

/// <summary>Where the in-process local proxy is in its life.</summary>
public enum LocalProxyState
{
    Stopped,

    /// <summary>Building and starting; the token warmup, and so any sign-in, happens here.</summary>
    Starting,

    Running,

    /// <summary>Refused to start or failed while starting; <see cref="LocalProxyHost.Failure"/> says why.</summary>
    Failed
}

/// <summary>
/// Runs local mode inside the tray process: the same application <c>--mode local</c> builds,
/// started and stopped on request. Each start builds afresh, so settings saved since the last
/// start apply. The tray runs in the signed-in user's session, so the interactive MSAL
/// sign-in local mode needs can open a browser as it does from a console.
/// </summary>
public sealed class LocalProxyHost : IAsyncDisposable
{
    private static readonly string[] LocalModeArguments = [DevConduitModeSelection.Argument, "local"];
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    private readonly SettingsFiles _settingsFiles;
    private readonly ILoggerProvider _logSink;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WebApplication? _app;
    private IReadOnlyList<ClientTrustStatus> _lastTrust = [];
    private CancellationTokenSource? _starting;
    private LocalProxyState _state = LocalProxyState.Stopped;

    public LocalProxyHost(SettingsFiles settingsFiles, ILoggerProvider logSink)
    {
        _settingsFiles = settingsFiles;
        _logSink = logSink;
        _logger = logSink.CreateLogger("Dev.Conduit.Tray");
    }

    /// <summary>Raised when <see cref="State"/> changes, on whichever thread changed it.</summary>
    public event EventHandler? StateChanged;

    public LocalProxyState State => _state;

    /// <summary>Why the last start failed: the fatal configuration problems, or the exception message.</summary>
    public string? Failure { get; private set; }

    /// <summary>True when the last failure was configuration that refused to start, rather than an error while starting.</summary>
    public bool FailedOnConfiguration { get; private set; }

    /// <summary>The local settings the running application was built with; null unless running.</summary>
    public LocalModeOptions? RunningOptions { get; private set; }

    /// <summary>
    /// What the certificate sync last found about each imported server: from the running
    /// application, or as it stood when it last stopped.
    /// </summary>
    public IReadOnlyList<ClientTrustStatus> TrustStatuses =>
        _app?.Services.GetService<ClientTrustStatusBoard>()?.Current ?? _lastTrust;

    public async Task StartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_app is not null)
            {
                return;
            }

            using var starting = new CancellationTokenSource();
            _starting = starting;
            Failure = null;
            FailedOnConfiguration = false;
            SetState(LocalProxyState.Starting);

            var (app, fatalMessage) = await Task.Run(Build).ConfigureAwait(false);
            if (app is null)
            {
                Fail(fatalMessage, configuration: true);
                return;
            }

            try
            {
                await app.StartAsync(starting.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (starting.IsCancellationRequested)
            {
                await DisposeQuietlyAsync(app).ConfigureAwait(false);
                _logger.LogInformation("The local proxy was stopped while it was starting.");
                SetState(LocalProxyState.Stopped);
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The local proxy could not start.");
                await DisposeQuietlyAsync(app).ConfigureAwait(false);
                Fail(exception.Message, configuration: false);
                return;
            }

            _app = app;
            RunningOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DevConduitOptions>>().Value.Local;
            SetState(LocalProxyState.Running);
        }
        finally
        {
            _starting = null;
            _gate.Release();
        }
    }

    /// <summary>Stops the local proxy, cancelling a start (and any sign-in) still in progress.</summary>
    public async Task StopAsync()
    {
        try
        {
            _starting?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The start finished between the read and the cancel.
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var app = _app;
            if (app?.Services.GetService<ClientTrustStatusBoard>() is { } board)
            {
                _lastTrust = board.Current;
            }

            _app = null;
            RunningOptions = null;

            if (app is not null)
            {
                using var timeout = new CancellationTokenSource(StopTimeout);
                try
                {
                    await app.StopAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "The local proxy did not stop cleanly.");
                }

                await DisposeQuietlyAsync(app).ConfigureAwait(false);
            }

            SetState(LocalProxyState.Stopped);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Asks the running application's certificate sync to run now, for example after Windows trust changed.</summary>
    public void RequestCertificateSync() => _app?.Services.GetService<ClientCertificateSync>()?.RequestSync();

    public async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await StartAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private (WebApplication? App, string FatalMessage) Build()
    {
        try
        {
            return DevConduitApplication.TryBuild(LocalModeArguments, runningAsService: false, _settingsFiles, _logSink, out var app, out var fatalMessage)
                ? (app, string.Empty)
                : (null, fatalMessage);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The local proxy could not be built.");
            return (null, exception.Message);
        }
    }

    private void Fail(string message, bool configuration)
    {
        Failure = message.Trim();
        FailedOnConfiguration = configuration;
        if (configuration)
        {
            _logger.LogError("{Message}", Failure);
        }

        SetState(LocalProxyState.Failed);
    }

    private void SetState(LocalProxyState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task DisposeQuietlyAsync(WebApplication app)
    {
        try
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The local proxy was not disposed cleanly.");
        }
    }
}
