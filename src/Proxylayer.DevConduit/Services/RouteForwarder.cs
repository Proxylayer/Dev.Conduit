using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Models;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Relays one request to its upstream and streams the response straight back. Bodies are
/// never buffered on the way through: they are copied in chunks and teed into capped
/// capture buffers as they pass.
/// </summary>
public sealed class RouteForwarder
{
    private const int CopyBufferSize = 64 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly RoutedTrafficRecorder _recorder;
    private readonly IRouteEgress _egress;
    private readonly DevConduitOptions _options;
    private readonly bool _capture;
    private readonly ILogger<RouteForwarder> _logger;

    public RouteForwarder(
        IHttpClientFactory httpClientFactory,
        RoutedTrafficRecorder recorder,
        IRouteEgress egress,
        IOptions<DevConduitOptions> options,
        ILogger<RouteForwarder> logger)
    {
        _httpClientFactory = httpClientFactory;
        _recorder = recorder;
        _egress = egress;
        _options = options.Value;

        // Local mode relays to a remote instance that already records the exchange, so
        // capturing here as well would store every body twice.
        _capture = _options.Mode != DevConduitMode.Local || _options.Local.CaptureEnabled;
        _logger = logger;
    }

    public async Task ForwardAsync(HttpContext context, RouteTarget target, string userId)
    {
        var request = context.Request;
        var upstreamUri = RouteTargetResolver.BuildUpstreamUri(target, request.QueryString);
        var destination = _egress.Destination(target, upstreamUri);
        var startedUtc = DateTimeOffset.UtcNow;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.Route.EffectiveTimeoutSeconds));
        var cancellationToken = timeout.Token;

        var capturedRequestHeaders = new List<HeaderItem>();
        CappedBuffer? requestCapture = null;
        var stopwatch = Stopwatch.StartNew();

        using var message = new HttpRequestMessage(new HttpMethod(request.Method), destination);

        if (HasBody(request))
        {
            Stream body = request.Body;
            if (_capture)
            {
                requestCapture = new CappedBuffer(_options.MaxRoutedStoredBodyBytes);
                body = new CapturingReadStream(request.Body, requestCapture);
            }

            var content = new StreamContent(body, CopyBufferSize);
            if (request.ContentLength is { } length)
            {
                content.Headers.ContentLength = length;
            }

            message.Content = content;
        }

        CopyRequestHeaders(request, message, capturedRequestHeaders);

        var client = _httpClientFactory.CreateClient(_egress.HttpClientName);

        HttpResponseMessage response;
        try
        {
            // Local mode acquires an Entra token here, so a sign-in failure is classified
            // and reported the same way an unreachable upstream is.
            await _egress
                .ApplyAsync(
                    new RouteEgressContext(request, message, target, upstreamUri, destination, capturedRequestHeaders),
                    cancellationToken)
                .ConfigureAwait(false);

            response = await client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            var (kind, detail) = Classify(exception, context.RequestAborted);
            await WriteFailureAsync(context, target, upstreamUri, kind, detail).ConfigureAwait(false);
            Record(
                userId, startedUtc, request, target, upstreamUri, capturedRequestHeaders, requestCapture,
                response: null, responseCapture: null, stopwatch.ElapsedMilliseconds, detail, kind);
            return;
        }

        using (response)
        {
            if (await _egress.ExplainAsync(response, target, cancellationToken).ConfigureAwait(false) is { } explanation)
            {
                stopwatch.Stop();
                await WriteExplanationAsync(context, response, explanation).ConfigureAwait(false);
                Record(
                    userId, startedUtc, request, target, upstreamUri, capturedRequestHeaders, requestCapture,
                    response, responseCapture: null, stopwatch.ElapsedMilliseconds,
                    explanation.Error, ProxyErrorKind.Unexpected);
                return;
            }

            var responseHeaders = CopyResponseHeaders(response, context.Response);
            var responseCapture = _capture ? new CappedBuffer(_options.MaxRoutedStoredBodyBytes) : null;

            try
            {
                await using var upstreamBody = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);

                await CopyAsync(upstreamBody, context.Response.Body, responseCapture, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Headers are already on the wire, so the only honest signal left is to
                // abort the response rather than pretend it completed.
                stopwatch.Stop();
                _logger.LogInformation(exception, "The response body from {Url} was cut short.", upstreamUri);
                context.Abort();
                Record(
                    userId, startedUtc, request, target, upstreamUri, capturedRequestHeaders, requestCapture,
                    response, responseCapture, stopwatch.ElapsedMilliseconds,
                    HttpFailureClassifier.Describe(exception), ProxyErrorKind.Connection, responseHeaders);
                return;
            }

            stopwatch.Stop();
            Record(
                userId, startedUtc, request, target, upstreamUri, capturedRequestHeaders, requestCapture,
                response, responseCapture, stopwatch.ElapsedMilliseconds, error: null, ProxyErrorKind.None, responseHeaders);
        }
    }

    /// <summary>
    /// Whether the inbound request carries a body. Content-Length settles it when present;
    /// otherwise chunked framing does, and failing both the verb decides.
    /// </summary>
    private static bool HasBody(HttpRequest request)
    {
        if (request.ContentLength is { } length)
        {
            return length > 0;
        }

        if (!StringValues.IsNullOrEmpty(request.Headers.TransferEncoding))
        {
            return true;
        }

        return !(HttpMethods.IsGet(request.Method)
                 || HttpMethods.IsHead(request.Method)
                 || HttpMethods.IsDelete(request.Method)
                 || HttpMethods.IsTrace(request.Method)
                 || HttpMethods.IsOptions(request.Method));
    }

    private void CopyRequestHeaders(
        HttpRequest request,
        HttpRequestMessage message,
        List<HeaderItem> captured)
    {
        var connectionTokens = RouteHeaderPolicy.ConnectionTokens(request.Headers.Connection);

        foreach (var header in request.Headers)
        {
            var name = header.Key;

            if (RouteHeaderPolicy.HopByHop.Contains(name)
                || RouteHeaderPolicy.Control.Contains(name)
                || connectionTokens.Contains(name))
            {
                continue;
            }

            // Dev.Conduit's own Entra token authenticates the developer to Dev.Conduit and
            // must never reach the upstream.
            if (string.Equals(name, RouteHeaderPolicy.AuthorizationHeader, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Host is set by the egress; Content-Length is taken from the inbound framing.
            if (string.Equals(name, DevConduitHeaderNames.Host, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A browser attaches every cookie it holds for the Dev.Conduit origin, Dev.Conduit's
            // own session cookie among them. Nothing of that set leaves by default.
            if (string.Equals(name, DevConduitHeaderNames.Cookie, StringComparison.OrdinalIgnoreCase))
            {
                CopyCookies(message, header.Value, captured);
                continue;
            }

            foreach (var value in header.Value)
            {
                RequestHeaderWriter.Apply(message, name, value ?? string.Empty, _logger);
                captured.Add(new HeaderItem(name, value ?? string.Empty));
            }
        }
    }

    private void CopyCookies(HttpRequestMessage message, StringValues values, List<HeaderItem> captured)
    {
        foreach (var value in values)
        {
            if (RouteCookiePolicy.Filter(value, _options.Route.ForwardCookies) is not { } forwarded)
            {
                continue;
            }

            RequestHeaderWriter.Apply(message, DevConduitHeaderNames.Cookie, forwarded, _logger);
            captured.Add(new HeaderItem(DevConduitHeaderNames.Cookie, forwarded, Enabled: true, IsSecret: true));
        }
    }

    private List<ResponseHeader> CopyResponseHeaders(HttpResponseMessage response, HttpResponse target)
    {
        target.StatusCode = (int)response.StatusCode;

        var captured = new List<ResponseHeader>();
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (RouteHeaderPolicy.HopByHop.Contains(header.Key))
            {
                continue;
            }

            var values = header.Value.ToArray();
            captured.AddRange(values.Select(value => new ResponseHeader(header.Key, value)));

            try
            {
                target.Headers[header.Key] = new StringValues(values);
            }
            catch (InvalidOperationException exception)
            {
                _logger.LogInformation(exception, "Dropping upstream response header {Name}: it was rejected.", header.Key);
            }
        }

        return captured;
    }

    private static async Task CopyAsync(Stream source, Stream destination, CappedBuffer? capture, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                capture?.Append(buffer.AsSpan(0, read));
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static (ProxyErrorKind Kind, string Message) Classify(Exception exception, CancellationToken clientToken) =>
        exception switch
        {
            HttpRequestException http => HttpFailureClassifier.Classify(http),
            _ when HttpFailureClassifier.TryFindEgressBlock(exception, out var blocked) =>
                (ProxyErrorKind.Blocked, blocked.Message),
            _ when HttpFailureClassifier.TryFindTargetRefusal(exception, out var refused) =>
                (ProxyErrorKind.TargetNotAllowed, refused.Message),
            OperationCanceledException when clientToken.IsCancellationRequested =>
                (ProxyErrorKind.Canceled, "The caller disconnected before the upstream responded."),
            OperationCanceledException =>
                (ProxyErrorKind.Timeout, "The upstream did not respond before the configured timeout elapsed."),
            _ => (ProxyErrorKind.Unexpected, HttpFailureClassifier.Describe(exception))
        };

    /// <summary>
    /// Answers 502 for an upstream that could not be reached, 400 for a destination in a
    /// blocked network, matching the other requests Dev.Conduit will not route, and 403 with
    /// <see cref="TargetAllowList.ErrorKind"/> for one the server's allow-list does not cover,
    /// matching the refusal of a target named in the request.
    /// </summary>
    private async Task WriteFailureAsync(HttpContext context, RouteTarget target, Uri upstreamUri, ProxyErrorKind kind, string detail)
    {
        var refused = kind is ProxyErrorKind.Blocked or ProxyErrorKind.TargetNotAllowed;
        _logger.Log(
            refused ? LogLevel.Warning : LogLevel.Information,
            "Forwarding to {Url} failed: {Kind}.",
            upstreamUri,
            kind);

        if (context.Response.HasStarted)
        {
            return;
        }

        var (status, errorKind) = kind switch
        {
            ProxyErrorKind.Blocked => (StatusCodes.Status400BadRequest, kind.ToString()),
            ProxyErrorKind.TargetNotAllowed => (StatusCodes.Status403Forbidden, TargetAllowList.ErrorKind),
            _ => (StatusCodes.Status502BadGateway, kind.ToString())
        };

        context.Response.Clear();
        context.Response.StatusCode = status;
        await context.Response
            .WriteAsJsonAsync(new RouteFailure(errorKind, detail, target.Origin.GetLeftPart(UriPartial.Authority), upstreamUri.ToString()))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces a response that describes a Dev.Conduit problem rather than an upstream one.
    /// The status is preserved so the caller still sees 401 or 403; only the opaque body is
    /// swapped for something that names what to fix.
    /// </summary>
    private async Task WriteExplanationAsync(HttpContext context, HttpResponseMessage response, RouteFailure explanation)
    {
        _logger.LogWarning("The remote Dev.Conduit answered {Status}: {Error}", (int)response.StatusCode, explanation.Error);

        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = (int)response.StatusCode;
        await context.Response.WriteAsJsonAsync(explanation).ConfigureAwait(false);
    }

    private void Record(
        string userId,
        DateTimeOffset startedUtc,
        HttpRequest request,
        RouteTarget target,
        Uri upstreamUri,
        IReadOnlyList<HeaderItem> requestHeaders,
        CappedBuffer? requestCapture,
        HttpResponseMessage? response,
        CappedBuffer? responseCapture,
        long durationMs,
        string? error,
        ProxyErrorKind kind,
        IReadOnlyList<ResponseHeader>? responseHeaders = null)
    {
        if (!_capture)
        {
            return;
        }

        try
        {
            _recorder.Enqueue(new RoutedExchange(
                userId,
                startedUtc,
                request.Method,
                upstreamUri.ToString(),
                request.Path.HasValue ? request.Path.Value! : "/",
                target.Alias,
                target.Origin.GetLeftPart(UriPartial.Authority),
                requestHeaders,
                requestCapture,
                request.ContentType,
                response is null ? null : (int)response.StatusCode,
                response?.ReasonPhrase,
                responseHeaders ?? [],
                responseCapture,
                response?.Content.Headers.ContentType?.ToString(),
                response?.Content.Headers.ContentEncoding.Count > 0,
                durationMs,
                error,
                kind == ProxyErrorKind.None ? null : kind.ToString()));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not queue routed traffic for {Url}.", upstreamUri);
        }
    }
}
