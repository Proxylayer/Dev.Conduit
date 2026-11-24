using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Models;

namespace Proxylayer.DevConduit.Services;

/// <summary>Executes the composed request from this machine and captures the full response.</summary>
public sealed class ProxyExecutor : IProxyExecutor
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DevConduitOptions _options;
    private readonly ILogger<ProxyExecutor> _logger;

    public ProxyExecutor(IHttpClientFactory httpClientFactory, IOptions<DevConduitOptions> options, ILogger<ProxyExecutor> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ProxyExecutionResult> ExecuteAsync(ProxyRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Method))
        {
            return ProxyExecutionResult.FromError(ProxyErrorKind.InvalidRequest, "An HTTP method is required.", 0);
        }

        if (!TryCreateUri(request.Url, out var uri, out var urlError))
        {
            return ProxyExecutionResult.FromError(ProxyErrorKind.InvalidRequest, urlError, 0);
        }

        var allowInvalidCertificates = request.AllowInvalidCertificates;
        if (allowInvalidCertificates && !_options.AllowInsecureTlsOptIn)
        {
            return ProxyExecutionResult.FromError(
                ProxyErrorKind.InvalidRequest,
                "Certificate validation cannot be disabled: DevConduit:AllowInsecureTlsOptIn is false.",
                0);
        }

        if (allowInvalidCertificates)
        {
            _logger.LogWarning("TLS certificate validation is disabled for the outbound call to {Host}.", uri.Host);
        }

        var client = _httpClientFactory.CreateClient(
            ProxyHttpClients.NameFor(request.FollowRedirects, allowInvalidCertificates));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.EffectiveTimeoutSeconds));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var message = BuildMessage(request, uri);
            using var response = await client
                .SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();

            return ProxyExecutionResult.FromResponse(Describe(response, bytes, stopwatch.ElapsedMilliseconds));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return ProxyExecutionResult.FromError(
                ProxyErrorKind.Canceled,
                "The request was canceled by the client.",
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return ProxyExecutionResult.FromError(
                ProxyErrorKind.Timeout,
                $"The request timed out after {request.EffectiveTimeoutSeconds} seconds.",
                stopwatch.ElapsedMilliseconds);
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            var (kind, message) = HttpFailureClassifier.Classify(exception);
            _logger.Log(
                kind is ProxyErrorKind.Blocked or ProxyErrorKind.TargetNotAllowed ? LogLevel.Warning : LogLevel.Information,
                exception,
                "Outbound call to {Url} failed: {Kind}.",
                uri,
                kind);
            return ProxyExecutionResult.FromError(kind, message, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or IOException or FormatException)
        {
            stopwatch.Stop();
            if (HttpFailureClassifier.TryFindEgressBlock(exception, out var blocked))
            {
                _logger.LogWarning(exception, "Outbound call to {Url} failed: {Kind}.", uri, ProxyErrorKind.Blocked);
                return ProxyExecutionResult.FromError(ProxyErrorKind.Blocked, blocked.Message, stopwatch.ElapsedMilliseconds);
            }

            if (HttpFailureClassifier.TryFindTargetRefusal(exception, out var refused))
            {
                _logger.LogWarning(exception, "Outbound call to {Url} failed: {Kind}.", uri, ProxyErrorKind.TargetNotAllowed);
                return ProxyExecutionResult.FromError(ProxyErrorKind.TargetNotAllowed, refused.Message, stopwatch.ElapsedMilliseconds);
            }

            _logger.LogInformation(exception, "Outbound call to {Url} could not be completed.", uri);
            return ProxyExecutionResult.FromError(
                ProxyErrorKind.Unexpected,
                HttpFailureClassifier.Describe(exception),
                stopwatch.ElapsedMilliseconds);
        }
    }

    private static bool TryCreateUri(string? url, out Uri uri, out string error)
    {
        uri = null!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(url))
        {
            error = "A URL is required.";
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
        {
            error = "The URL is not a valid absolute URI (for example https://example.com/path).";
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = $"The scheme '{parsed.Scheme}' is not supported. Use http or https.";
            return false;
        }

        uri = parsed;
        return true;
    }

    private HttpRequestMessage BuildMessage(ProxyRequest request, Uri uri)
    {
        // The verb is passed through verbatim so custom methods survive intact.
        var message = new HttpRequestMessage(new HttpMethod(request.Method.Trim()), uri);

        var hasBody = !string.IsNullOrEmpty(request.Body);
        if (hasBody)
        {
            var contentType = string.IsNullOrWhiteSpace(request.ContentType) ? null : request.ContentType.Trim();
            var content = new StringContent(request.Body!, new UTF8Encoding(false));
            content.Headers.ContentType = null;
            if (contentType is not null && System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var parsed))
            {
                content.Headers.ContentType = parsed;
            }

            message.Content = content;
        }

        foreach (var header in request.Headers)
        {
            if (!header.Enabled || string.IsNullOrWhiteSpace(header.Name))
            {
                continue;
            }

            ApplyHeader(message, header.Name.Trim(), header.Value ?? string.Empty);
        }

        return message;
    }

    private void ApplyHeader(HttpRequestMessage message, string name, string value) =>
        RequestHeaderWriter.Apply(message, name, value, _logger);

    private static ProxyResponse Describe(HttpResponseMessage response, byte[] bytes, long durationMs)
    {
        var headers = new List<ResponseHeader>();
        foreach (var header in response.Headers)
        {
            headers.AddRange(header.Value.Select(value => new ResponseHeader(header.Key, value)));
        }

        foreach (var header in response.Content.Headers)
        {
            headers.AddRange(header.Value.Select(value => new ResponseHeader(header.Key, value)));
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var contentType = response.Content.Headers.ContentType?.ToString();

        string? body;
        bool isBase64;
        if (HttpContentTypes.IsTextual(mediaType))
        {
            var encoding = HttpContentTypes.ResolveEncoding(response.Content.Headers.ContentType?.CharSet);
            body = encoding.GetString(bytes);
            isBase64 = false;
        }
        else if (bytes.Length == 0)
        {
            body = string.Empty;
            isBase64 = false;
        }
        else
        {
            body = Convert.ToBase64String(bytes);
            isBase64 = true;
        }

        return new ProxyResponse(
            (int)response.StatusCode,
            response.ReasonPhrase,
            response.Version.ToString(),
            headers,
            durationMs,
            bytes.LongLength,
            body,
            isBase64,
            BodyTruncated: false,
            contentType);
    }
}
