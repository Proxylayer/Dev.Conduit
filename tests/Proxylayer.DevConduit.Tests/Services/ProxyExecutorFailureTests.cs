using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Proxylayer.DevConduit.Models;
using Proxylayer.DevConduit.Services;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Services;

/// <summary>
/// Failure classification as the console executor reports it. This is the public surface
/// on which a timeout is told apart from a caller cancellation and a malformed URL is
/// refused before anything is sent.
/// </summary>
public sealed class ProxyExecutorFailureTests
{
    [Theory]
    [InlineData("", "A URL is required.")]
    [InlineData("   ", "A URL is required.")]
    [InlineData("not a uri", "The URL is not a valid absolute URI (for example https://example.com/path).")]
    [InlineData("/relative/path", "The URL is not a valid absolute URI (for example https://example.com/path).")]
    [InlineData("http//missing-colon", "The URL is not a valid absolute URI (for example https://example.com/path).")]
    public async Task ExecuteAsync_RefusesAMalformedUrlBeforeSendingAnything(string url, string expected)
    {
        var handler = new RecordingHandler();

        var result = await Executor(handler).ExecuteAsync(new ProxyRequest("GET", url, []), CancellationToken.None);

        Assert.Equal(ProxyErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal(expected, result.Error);
        Assert.Null(handler.Sent);
    }

    [Theory]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///c:/windows/win.ini")]
    public async Task ExecuteAsync_RefusesASchemeItDoesNotSpeak(string url)
    {
        var handler = new RecordingHandler();

        var result = await Executor(handler).ExecuteAsync(new ProxyRequest("GET", url, []), CancellationToken.None);

        Assert.Equal(ProxyErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Contains("Use http or https.", result.Error);
        Assert.Null(handler.Sent);
    }

    [Fact]
    public async Task ExecuteAsync_RefusesARequestWithoutAMethod()
    {
        var result = await Executor(new RecordingHandler())
            .ExecuteAsync(new ProxyRequest("  ", "https://example.com", []), CancellationToken.None);

        Assert.Equal(ProxyErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal("An HTTP method is required.", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsAStalledUpstreamAsATimeout()
    {
        var handler = new RecordingHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var result = await Executor(handler).ExecuteAsync(
            new ProxyRequest("GET", "https://example.com", [], TimeoutSeconds: 1),
            CancellationToken.None);

        Assert.Equal(ProxyErrorKind.Timeout, result.ErrorKind);
        Assert.Equal("The request timed out after 1 seconds.", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_TellsACallerCancellationApartFromATimeout()
    {
        using var caller = new CancellationTokenSource();
        var handler = new RecordingHandler(async (_, token) =>
        {
            caller.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var result = await Executor(handler).ExecuteAsync(
            new ProxyRequest("GET", "https://example.com", [], TimeoutSeconds: 600),
            caller.Token);

        Assert.Equal(ProxyErrorKind.Canceled, result.ErrorKind);
        Assert.Equal("The request was canceled by the client.", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ClassifiesAnUnreachableHost()
    {
        var handler = new RecordingHandler((_, _) =>
            throw new HttpRequestException(HttpRequestError.NameResolutionError, "no such host"));

        var result = await Executor(handler).ExecuteAsync(
            new ProxyRequest("GET", "https://nowhere.invalid", []),
            CancellationToken.None);

        Assert.Equal(ProxyErrorKind.Dns, result.ErrorKind);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task ExecuteAsync_ClassifiesARefusedConnection()
    {
        var handler = new RecordingHandler((_, _) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"));

        var result = await Executor(handler).ExecuteAsync(
            new ProxyRequest("GET", "http://127.0.0.1:1", []),
            CancellationToken.None);

        Assert.Equal(ProxyErrorKind.Connection, result.ErrorKind);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsATargetTheAllowListRefusesInItsOwnErrorKind()
    {
        var refused = new TargetNotAllowedException("evil.example", 443, [IPAddress.Parse("203.0.113.5")]);
        var handler = new RecordingHandler((_, _) => throw new HttpRequestException("x", refused));

        var result = await Executor(handler).ExecuteAsync(
            new ProxyRequest("GET", "https://evil.example", []),
            CancellationToken.None);

        Assert.Equal(ProxyErrorKind.TargetNotAllowed, result.ErrorKind);
        Assert.Equal(refused.Message, result.Error);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task ExecuteAsync_RefusesToDisableCertificateValidationWhenPolicyForbidsIt()
    {
        var handler = new RecordingHandler();
        var executor = new ProxyExecutor(
            new StubHttpClientFactory(handler),
            TestOptions.Create(options => options.AllowInsecureTlsOptIn = false),
            NullLogger<ProxyExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            new ProxyRequest("GET", "https://example.com", [], AllowInvalidCertificates: true),
            CancellationToken.None);

        Assert.Equal(ProxyErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Contains("AllowInsecureTlsOptIn", result.Error);
        Assert.Null(handler.Sent);
    }

    private static ProxyExecutor Executor(RecordingHandler handler) =>
        new(new StubHttpClientFactory(handler), TestOptions.Create(), NullLogger<ProxyExecutor>.Instance);
}
