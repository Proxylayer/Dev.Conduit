using System.Threading.Channels;
using Proxylayer.DevConduit.Models;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Accepts finished exchanges and writes them to the routed store on a background loop,
/// so capture never adds latency to a forwarded call and never fails one. The queue is
/// bounded: under a flood the oldest pending capture is dropped rather than the memory.
/// </summary>
public sealed class RoutedTrafficRecorder : BackgroundService
{
    private const int QueueCapacity = 256;

    private readonly Channel<RoutedExchange> _queue = Channel.CreateBounded<RoutedExchange>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    private readonly IRoutedTrafficStore _store;
    private readonly SecretHeaderPolicy _secrets;
    private readonly ILogger<RoutedTrafficRecorder> _logger;

    public RoutedTrafficRecorder(
        IRoutedTrafficStore store,
        SecretHeaderPolicy secrets,
        ILogger<RoutedTrafficRecorder> logger)
    {
        _store = store;
        _secrets = secrets;
        _logger = logger;
    }

    /// <summary>Queues an exchange for storage. Never throws and never blocks.</summary>
    public void Enqueue(RoutedExchange exchange)
    {
        if (!_queue.Writer.TryWrite(exchange))
        {
            _logger.LogDebug("Routed capture for {Url} was dropped: the write queue is saturated.", exchange.UpstreamUrl);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var exchange in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await _store.AppendAsync(exchange.UserId, Build(exchange), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A capture failure must never be visible to the developer's own traffic.
                _logger.LogWarning(exception, "Could not record routed traffic for {Url}.", exchange.UpstreamUrl);
            }
        }
    }

    private RoutedEntry Build(RoutedExchange exchange)
    {
        var (requestBody, requestIsBase64) = Decode(exchange.RequestBody, exchange.RequestContentType, encoded: false);
        var (responseBody, responseIsBase64) = Decode(exchange.ResponseBody, exchange.ResponseContentType, exchange.ResponseEncoded);

        return new RoutedEntry(
            RoutedEntry.NewId(),
            exchange.TimestampUtc,
            exchange.Method,
            exchange.UpstreamUrl,
            exchange.InboundPath,
            exchange.Alias,
            exchange.TargetOrigin,
            exchange.RequestHeaders.Select(_secrets.Redact).ToArray(),
            requestBody,
            requestIsBase64,
            exchange.RequestBody?.Truncated ?? false,
            exchange.RequestBody?.TotalBytes ?? 0,
            exchange.RequestContentType,
            exchange.StatusCode,
            exchange.ReasonPhrase,
            exchange.ResponseHeaders.Select(_secrets.Redact).ToArray(),
            responseBody,
            responseIsBase64,
            exchange.ResponseBody?.Truncated ?? false,
            exchange.ResponseBody?.TotalBytes ?? 0,
            exchange.ResponseContentType,
            exchange.DurationMs,
            exchange.Error,
            exchange.ErrorKind);
    }

    /// <summary>
    /// Textual payloads are stored as text so they are searchable; everything else, and
    /// anything still content-encoded, is stored as base64.
    /// </summary>
    private static (string? Body, bool IsBase64) Decode(CappedBuffer? buffer, string? contentType, bool encoded)
    {
        if (buffer is null || buffer.TotalBytes == 0)
        {
            return (null, false);
        }

        var bytes = buffer.Retained();
        if (bytes.Length == 0)
        {
            return (null, false);
        }

        var mediaType = MediaTypeOf(contentType);
        if (!encoded && HttpContentTypes.IsTextual(mediaType))
        {
            return (HttpContentTypes.ResolveEncoding(CharsetOf(contentType)).GetString(bytes), false);
        }

        return (Convert.ToBase64String(bytes), true);
    }

    private static string? MediaTypeOf(string? contentType) =>
        string.IsNullOrWhiteSpace(contentType)
            ? null
            : contentType.Split(';', 2)[0].Trim();

    private static string? CharsetOf(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        foreach (var part in contentType.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1))
        {
            if (part.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))
            {
                return part["charset=".Length..];
            }
        }

        return null;
    }
}
