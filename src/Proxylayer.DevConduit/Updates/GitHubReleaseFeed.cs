using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace Proxylayer.DevConduit.Updates;

public enum FeedCheckStatus
{
    /// <summary>The newest release is not newer than this build.</summary>
    UpToDate,

    /// <summary>A newer release counts.</summary>
    UpdateAvailable,

    /// <summary>The feed has no release that counts (none published, or none with both assets).</summary>
    NoRelease,

    /// <summary>GitHub's rate limit was reached; no request is made until it resets.</summary>
    RateLimited,

    /// <summary>The feed could not be reached.</summary>
    Offline,

    /// <summary>The feed answered with an error.</summary>
    Failed
}

/// <summary>One look at the feed.</summary>
/// <param name="Status">What the look found.</param>
/// <param name="Release">The newest release that counts, when there is one, newer or not.</param>
/// <param name="Message">A sentence for the log and the About page.</param>
public sealed record FeedCheck(FeedCheckStatus Status, UpdateRelease? Release, string Message);

/// <summary>
/// The GitHub Releases feed of one repository, read anonymously. Sends a generic
/// <c>User-Agent: Dev.Conduit-Updater/&lt;version&gt;</c> and nothing that identifies the user or
/// machine. Remembers the last answer's ETag and sends it as <c>If-None-Match</c>, so an unchanged
/// feed costs a 304, which GitHub does not count against the anonymous limit of 60 requests an
/// hour. When the limit is reached (403 or 429 with rate-limit headers) it makes no request
/// until the reset time the answer gave.
/// </summary>
public sealed class GitHubReleaseFeed : IDisposable
{
    /// <summary>The largest MSI accepted.</summary>
    public const long MaxMsiBytes = 512L * 1024 * 1024;

    private const long MaxJsonBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DefaultBackoff = TimeSpan.FromMinutes(15);

    private readonly HttpClient _http;
    private readonly GitHubRepository _repository;
    private readonly bool _includePrerelease;
    private readonly Version _current;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private string? _etag;
    private ReleaseParse? _cached;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;
    private Version? _announced;

    public GitHubReleaseFeed(
        GitHubRepository repository,
        bool includePrerelease,
        Version current,
        HttpMessageHandler handler,
        TimeProvider time,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _repository = repository;
        _includePrerelease = includePrerelease;
        _current = ReleaseVersion.Normalize(current);
        _time = time;
        _logger = logger;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Dev.Conduit-Updater", ReleaseVersion.Format(current)));
    }

    public GitHubRepository Repository => _repository;

    /// <summary>
    /// The feed of <paramref name="options"/>, or null when updates are off or the feed address is
    /// not a GitHub repository (the reason is in <paramref name="reason"/>).
    /// </summary>
    public static GitHubReleaseFeed? Create(UpdateOptions options, Version current, TimeProvider time, ILogger logger, out string reason, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            reason = $"{UpdateOptions.SectionName}:{nameof(UpdateOptions.FeedUrl)} is empty, so update checks are off.";
            return null;
        }

        if (!GitHubRepository.TryParse(options.FeedUrl, out var repository))
        {
            reason = $"{UpdateOptions.SectionName}:{nameof(UpdateOptions.FeedUrl)} '{options.FeedUrl}' is not https://github.com/<owner>/<repository>, so update checks are off.";
            return null;
        }

        reason = string.Empty;
        return new GitHubReleaseFeed(repository, options.IncludePrerelease, current, handler ?? CreateHandler(), time, logger);
    }

    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        UseCookies = false
    };

    /// <summary>Looks for the newest release that counts and compares it with this build.</summary>
    public async Task<FeedCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (now < _blockedUntil)
        {
            var waiting = $"GitHub's rate limit was reached; the next check is after {_blockedUntil.ToLocalTime():t}.";
            _logger.LogDebug("Update check skipped: {Message}", waiting);
            return new FeedCheck(FeedCheckStatus.RateLimited, _cached?.Release, waiting);
        }

        var uri = _includePrerelease ? _repository.ReleaseListUri : _repository.LatestReleaseUri;
        using var request = ApiRequest(uri);
        if (_etag is not null && _cached is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
        }

        ReleaseParse parse;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified && _cached is not null)
            {
                parse = _cached;
            }
            else if (IsRateLimited(response, now, out var resetAt))
            {
                _blockedUntil = resetAt;
                var limited = $"GitHub's rate limit was reached; checking again after {resetAt.ToLocalTime():t}.";
                _logger.LogDebug("Update check: {Message}", limited);
                return new FeedCheck(FeedCheckStatus.RateLimited, _cached?.Release, limited);
            }
            else if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Forget();
                return Report(new FeedCheck(FeedCheckStatus.NoRelease, null, $"{_repository.Owner}/{_repository.Name} has no published release."));
            }
            else if (!response.IsSuccessStatusCode)
            {
                return Report(new FeedCheck(FeedCheckStatus.Failed, _cached?.Release,
                    $"The feed answered {(int)response.StatusCode} {response.ReasonPhrase}."));
            }
            else
            {
                var json = await ReadLimitedAsync(response.Content, MaxJsonBytes, timeout.Token).ConfigureAwait(false);
                parse = _includePrerelease
                    ? GitHubReleaseParser.ParseList(json, includePrerelease: true)
                    : GitHubReleaseParser.ParseRelease(json, includePrerelease: false);
                _etag = response.Headers.ETag?.ToString();
                _cached = parse;
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                                              && exception is HttpRequestException or OperationCanceledException or IOException)
        {
            return Report(new FeedCheck(FeedCheckStatus.Offline, _cached?.Release, $"The feed could not be reached: {exception.Message}"));
        }
        catch (InvalidDataException exception)
        {
            return Report(new FeedCheck(FeedCheckStatus.Failed, _cached?.Release, exception.Message));
        }

        if (parse.Release is not { } release)
        {
            return Report(new FeedCheck(FeedCheckStatus.NoRelease, null, parse.Reason));
        }

        if (!ReleaseVersion.IsNewer(release.Version, _current))
        {
            return Report(new FeedCheck(FeedCheckStatus.UpToDate, release,
                $"Up to date: the newest release is {release.Tag}, this is {ReleaseVersion.Format(_current)}."));
        }

        var found = new FeedCheck(FeedCheckStatus.UpdateAvailable, release,
            $"Dev.Conduit {release.Tag} is available (this is {ReleaseVersion.Format(_current)}).");
        if (release.Version == _announced)
        {
            _logger.LogDebug("Update check: {Message}", found.Message);
        }
        else
        {
            _announced = release.Version;
            _logger.LogInformation("{Message}", found.Message);
        }

        return found;
    }

    /// <summary>The published release tagged <c>vX.Y.Z</c>, if it counts; null otherwise.</summary>
    public async Task<ReleaseParse> FindReleaseAsync(Version version, CancellationToken cancellationToken)
    {
        using var request = ApiRequest(_repository.ReleaseByTagUri(version));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new ReleaseParse(null, $"The feed has no published release v{ReleaseVersion.Format(version)} ({(int)response.StatusCode}).");
        }

        var json = await ReadLimitedAsync(response.Content, MaxJsonBytes, timeout.Token).ConfigureAwait(false);
        var parse = GitHubReleaseParser.ParseRelease(json, _includePrerelease);
        return parse.Release is { } release && release.Version != ReleaseVersion.Normalize(version)
            ? new ReleaseParse(null, $"The feed answered with {release.Tag} when asked for v{ReleaseVersion.Format(version)}.")
            : parse;
    }

    /// <summary>Downloads a small text asset, such as the checksum file.</summary>
    public async Task<string> DownloadTextAsync(Uri uri, int maxBytes, CancellationToken cancellationToken)
    {
        using var request = AssetRequest(uri);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadLimitedAsync(response.Content, maxBytes, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Downloads an asset to a new file, refusing one larger than <paramref name="maxBytes"/>.</summary>
    public async Task DownloadFileAsync(Uri uri, string path, long maxBytes, CancellationToken cancellationToken)
    {
        using var request = AssetRequest(uri);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new InvalidDataException($"{uri} is larger than {maxBytes / (1024 * 1024)} MB.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException($"{uri} is larger than {maxBytes / (1024 * 1024)} MB.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// True for a 403 or 429 that GitHub marks as a rate limit: <c>x-ratelimit-remaining: 0</c>
    /// or a <c>retry-after</c>. <paramref name="resetAt"/> is when to ask again: the
    /// <c>x-ratelimit-reset</c> time, else now plus <c>retry-after</c>, else fifteen minutes on.
    /// </summary>
    public static bool IsRateLimited(HttpResponseMessage response, DateTimeOffset now, out DateTimeOffset resetAt)
    {
        ArgumentNullException.ThrowIfNull(response);
        resetAt = now;
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
        {
            return false;
        }

        var exhausted = Header(response, "x-ratelimit-remaining") == "0";
        var retryAfter = response.Headers.RetryAfter;
        if (!exhausted && retryAfter is null && response.StatusCode != HttpStatusCode.TooManyRequests)
        {
            return false;
        }

        if (exhausted && long.TryParse(Header(response, "x-ratelimit-reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(epoch);
            resetAt = reset > now ? reset : now + DefaultBackoff;
        }
        else if (retryAfter?.Delta is { } delta)
        {
            resetAt = now + delta;
        }
        else if (retryAfter?.Date is { } date && date > now)
        {
            resetAt = date;
        }
        else
        {
            resetAt = now + DefaultBackoff;
        }

        return true;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim() : null;

    private HttpRequestMessage ApiRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static HttpRequestMessage AssetRequest(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException($"{uri} is not an https address.");
        }

        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return request;
    }

    private FeedCheck Report(FeedCheck check)
    {
        _logger.LogDebug("Update check: {Message}", check.Message);
        return check;
    }

    private void Forget()
    {
        _etag = null;
        _cached = null;
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes)
        {
            throw new InvalidDataException("The feed's answer is larger than expected.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException("The feed's answer is larger than expected.");
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
