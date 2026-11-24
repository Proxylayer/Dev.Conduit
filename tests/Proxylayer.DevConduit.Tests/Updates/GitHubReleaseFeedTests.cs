using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Proxylayer.DevConduit.Tests.Support;
using Proxylayer.DevConduit.Updates;

namespace Proxylayer.DevConduit.Tests.Updates;

/// <summary>
/// The feed client against a fake handler: what it sends, ETags and 304s, rate limiting, being
/// offline, and comparing with the running version. No request leaves the process.
/// </summary>
public sealed class GitHubReleaseFeedTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly GitHubRepository Repository = new("Proxylayer", "Dev.Conduit");

    private static GitHubReleaseFeed Feed(HttpMessageHandler handler, TimeProvider time, string current = "1.0.0", bool includePrerelease = false) =>
        new(Repository, includePrerelease, Version.Parse(current), handler, time, NullLogger.Instance);

    private static HttpResponseMessage Json(string json, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        if (etag is not null)
        {
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        }

        return response;
    }

    [Fact]
    public async Task Check_SendsAGenericUserAgentAndTheGitHubAcceptHeaderToReleasesLatest()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(ReleaseJson.Complete("1.0.0").ToJsonString())));
        using var feed = Feed(handler, new ManualClock(Start), current: "1.0.0");

        await feed.CheckAsync(CancellationToken.None);

        var sent = Assert.IsType<HttpRequestMessage>(handler.Sent);
        Assert.Equal("https://api.github.com/repos/Proxylayer/Dev.Conduit/releases/latest", sent.RequestUri!.AbsoluteUri);
        Assert.Equal($"Dev.Conduit-Updater/{ReleaseVersion.Format(new Version(1, 0, 0))}", sent.Headers.UserAgent.ToString());
        Assert.Contains(sent.Headers.Accept, accept => accept.MediaType == "application/vnd.github+json");
        Assert.Null(sent.Headers.Authorization);
        Assert.False(sent.Headers.Contains("If-None-Match"));
    }

    [Theory]
    [InlineData("1.0.0", FeedCheckStatus.UpdateAvailable)]
    [InlineData("1.1.0", FeedCheckStatus.UpToDate)]
    [InlineData("1.2.0", FeedCheckStatus.UpToDate)]
    public async Task Check_ComparesTheNewestReleaseWithTheRunningVersion(string current, FeedCheckStatus expected)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(ReleaseJson.Complete("1.1.0").ToJsonString())));
        using var feed = Feed(handler, new ManualClock(Start), current);

        var check = await feed.CheckAsync(CancellationToken.None);

        Assert.Equal(expected, check.Status);
        Assert.Equal(new Version(1, 1, 0), check.Release?.Version);
    }

    [Fact]
    public async Task Check_SendsTheETagBackAndAnswersA304FromWhatItRemembers()
    {
        var calls = 0;
        var handler = new RecordingHandler((request, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? Json(ReleaseJson.Complete("1.1.0").ToJsonString(), "\"abc123\"")
                : new HttpResponseMessage(HttpStatusCode.NotModified));
        });
        using var feed = Feed(handler, new ManualClock(Start));

        var first = await feed.CheckAsync(CancellationToken.None);
        var second = await feed.CheckAsync(CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal("\"abc123\"", Assert.Single(handler.Sent!.Headers.GetValues("If-None-Match")));
        Assert.Equal(FeedCheckStatus.UpdateAvailable, first.Status);
        Assert.Equal(FeedCheckStatus.UpdateAvailable, second.Status);
        Assert.Equal(first.Release, second.Release);
    }

    [Fact]
    public async Task Check_RateLimitedWithResetHeaders_MakesNoRequestUntilTheReset()
    {
        var clock = new ManualClock(Start);
        var reset = Start.AddMinutes(40);
        var calls = 0;
        var handler = new RecordingHandler((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"API rate limit exceeded\"}") };
                limited.Headers.Add("x-ratelimit-remaining", "0");
                limited.Headers.Add("x-ratelimit-reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
                return Task.FromResult(limited);
            }

            return Task.FromResult(Json(ReleaseJson.Complete("1.1.0").ToJsonString()));
        });
        using var feed = Feed(handler, clock);

        Assert.Equal(FeedCheckStatus.RateLimited, (await feed.CheckAsync(CancellationToken.None)).Status);

        clock.Advance(TimeSpan.FromMinutes(39));
        Assert.Equal(FeedCheckStatus.RateLimited, (await feed.CheckAsync(CancellationToken.None)).Status);
        Assert.Equal(1, calls);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(FeedCheckStatus.UpdateAvailable, (await feed.CheckAsync(CancellationToken.None)).Status);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void IsRateLimited_A429WithRetryAfterWaitsThatLong()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));

        Assert.True(GitHubReleaseFeed.IsRateLimited(response, Start, out var resetAt));
        Assert.Equal(Start.AddSeconds(90), resetAt);
    }

    [Fact]
    public void IsRateLimited_APlain403IsNotARateLimit()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);

        Assert.False(GitHubReleaseFeed.IsRateLimited(response, Start, out _));
    }

    [Fact]
    public void IsRateLimited_AResetInThePastBacksOffFifteenMinutes()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("x-ratelimit-remaining", "0");
        response.Headers.Add("x-ratelimit-reset", Start.AddMinutes(-5).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.True(GitHubReleaseFeed.IsRateLimited(response, Start, out var resetAt));
        Assert.Equal(Start.AddMinutes(15), resetAt);
    }

    [Fact]
    public async Task Check_OfflineIsReportedNotThrown()
    {
        var handler = new RecordingHandler((_, _) => throw new HttpRequestException("No such host is known."));
        using var feed = Feed(handler, new ManualClock(Start));

        var check = await feed.CheckAsync(CancellationToken.None);

        Assert.Equal(FeedCheckStatus.Offline, check.Status);
        Assert.Contains("No such host", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_ANotFoundMeansNoPublishedRelease()
    {
        var handler = new RecordingHandler(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}");
        using var feed = Feed(handler, new ManualClock(Start));

        Assert.Equal(FeedCheckStatus.NoRelease, (await feed.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Check_AServerErrorIsAFailure()
    {
        var handler = new RecordingHandler(HttpStatusCode.BadGateway, string.Empty);
        using var feed = Feed(handler, new ManualClock(Start));

        Assert.Equal(FeedCheckStatus.Failed, (await feed.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Check_AReleaseWithoutTheAssetsIsNoRelease()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(ReleaseJson.Release("v1.1.0", assets: ["Dev.Conduit-1.1.0.msi"]).ToJsonString())));
        using var feed = Feed(handler, new ManualClock(Start));

        Assert.Equal(FeedCheckStatus.NoRelease, (await feed.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Check_WithPrereleasesAsksForTheListAndTakesTheNewest()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(ReleaseJson.List(
            ReleaseJson.Complete("1.3.0", draft: true),
            ReleaseJson.Complete("1.2.0", prerelease: true),
            ReleaseJson.Complete("1.1.0")))));
        using var feed = Feed(handler, new ManualClock(Start), includePrerelease: true);

        var check = await feed.CheckAsync(CancellationToken.None);

        Assert.StartsWith("https://api.github.com/repos/Proxylayer/Dev.Conduit/releases?", handler.Sent!.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(new Version(1, 2, 0), check.Release?.Version);
    }

    [Fact]
    public async Task FindRelease_AsksForTheTagAndRefusesAnotherVersion()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(ReleaseJson.Complete("1.2.0").ToJsonString())));
        using var feed = Feed(handler, new ManualClock(Start));

        var wrong = await feed.FindReleaseAsync(new Version(1, 3, 0), CancellationToken.None);
        Assert.Equal("https://api.github.com/repos/Proxylayer/Dev.Conduit/releases/tags/v1.3.0", handler.Sent!.RequestUri!.AbsoluteUri);
        Assert.Null(wrong.Release);

        var right = await feed.FindReleaseAsync(new Version(1, 2, 0), CancellationToken.None);
        Assert.Equal(new Version(1, 2, 0), right.Release?.Version);
    }

    [Fact]
    public async Task DownloadFile_RefusesMoreThanTheLimit()
    {
        using var root = new TempDirectory();
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) }));
        using var feed = Feed(handler, new ManualClock(Start));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            feed.DownloadFileAsync(new Uri("https://example.test/a.msi"), Path.Combine(root.Path, "a.msi"), 1024, CancellationToken.None));
    }

    [Fact]
    public async Task DownloadText_RefusesAnAddressThatIsNotHttps()
    {
        var handler = new RecordingHandler();
        using var feed = Feed(handler, new ManualClock(Start));

        await Assert.ThrowsAsync<InvalidDataException>(() => feed.DownloadTextAsync(new Uri("http://example.test/a.sha256"), 1024, CancellationToken.None));
        Assert.Null(handler.Sent);
    }

    [Fact]
    public void Create_IsNullWhenTheFeedIsEmptyOrNotGitHub()
    {
        Assert.Null(GitHubReleaseFeed.Create(new UpdateOptions { FeedUrl = "" }, new Version(1, 0, 0), TimeProvider.System, NullLogger.Instance, out var off, new RecordingHandler()));
        Assert.Contains("empty", off, StringComparison.Ordinal);

        Assert.Null(GitHubReleaseFeed.Create(new UpdateOptions { FeedUrl = "https://example.com/x/y" }, new Version(1, 0, 0), TimeProvider.System, NullLogger.Instance, out var other, new RecordingHandler()));
        Assert.Contains("not https://github.com", other, StringComparison.Ordinal);
    }
}
