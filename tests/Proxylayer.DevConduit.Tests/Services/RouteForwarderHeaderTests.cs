using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Models;
using Proxylayer.DevConduit.Services;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Services;

/// <summary>
/// The header policy as the forwarder actually applies it. The filtering loop is private,
/// so it is exercised through one real forward against a recording handler: no socket is
/// opened and no port is bound.
/// </summary>
public sealed class RouteForwarderHeaderTests
{
    private const string UpstreamOrigin = "http://upstream.test:9099";

    [Fact]
    public async Task ForwardAsync_AddressesTheUpstreamWithThePathAndQuery()
    {
        var handler = new RecordingHandler();

        await ForwardAsync(handler, request => request.QueryString = new QueryString("?page=2&q=a%20b"));

        Assert.Equal($"{UpstreamOrigin}/api/cases?page=2&q=a%20b", handler.Sent!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task ForwardAsync_NeverRelaysTheCallersDevConduitCredential()
    {
        var handler = new RecordingHandler();

        await ForwardAsync(handler, request =>
        {
            request.Headers["Authorization"] = "Bearer devconduit-session-token";
        });

        Assert.False(handler.Sent!.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task ForwardAsync_RenamesTheUpstreamCredentialToAuthorization()
    {
        var handler = new RecordingHandler();

        await ForwardAsync(handler, request =>
        {
            request.Headers["Authorization"] = "Bearer devconduit-session-token";
            request.Headers[DevConduitHeaderNames.UpstreamAuthorization] = "Bearer upstream-token";
        });

        Assert.Equal("Bearer upstream-token", Assert.Single(handler.Sent!.Headers.GetValues("Authorization")));
        Assert.False(handler.Sent.Headers.Contains(DevConduitHeaderNames.UpstreamAuthorization));
    }

    [Fact]
    public async Task ForwardAsync_StripsTheDevConduitControlHeaders()
    {
        var handler = new RecordingHandler();

        await ForwardAsync(handler, request =>
        {
            request.Headers[DevConduitHeaderNames.Target] = UpstreamOrigin;
            request.Headers["X-DevConduit-Client"] = "1";
            request.Headers[DevConduitHeaderNames.UpstreamAuthorization] = "Bearer upstream-token";
        });

        Assert.False(handler.Sent!.Headers.Contains(DevConduitHeaderNames.Target));
        Assert.False(handler.Sent.Headers.Contains("X-DevConduit-Client"));
        Assert.False(handler.Sent.Headers.Contains(DevConduitHeaderNames.UpstreamAuthorization));
    }

    [Fact]
    public async Task ForwardAsync_NeverRelaysTheLocalSharedSecret()
    {
        var handler = new RecordingHandler();

        await ForwardAsync(handler, request =>
        {
            request.Headers[DevConduitHeaderNames.LocalSecret] = "the-shared-secret";
        });

        Assert.False(handler.Sent!.Headers.Contains(DevConduitHeaderNames.LocalSecret));
    }

    [Fact]
    public async Task ForwardAsync_StripsHopByHopHeadersAndEveryTokenTheConnectionHeaderNames()
    {
        var handler = new RecordingHandler();

        await ForwardAsync(handler, request =>
        {
            request.Headers["Connection"] = "keep-alive, X-Custom-Hop";
            request.Headers["X-Custom-Hop"] = "should not travel";
            request.Headers["Keep-Alive"] = "timeout=5";
            request.Headers["Proxy-Authorization"] = "Basic abc";
            request.Headers["Upgrade"] = "websocket";
            request.Headers["X-Kept"] = "travels";
        });

        Assert.False(handler.Sent!.Headers.Contains("Connection"));
        Assert.False(handler.Sent.Headers.Contains("X-Custom-Hop"));
        Assert.False(handler.Sent.Headers.Contains("Keep-Alive"));
        Assert.False(handler.Sent.Headers.Contains("Proxy-Authorization"));
        Assert.False(handler.Sent.Headers.Contains("Upgrade"));
        Assert.Equal("travels", Assert.Single(handler.Sent.Headers.GetValues("X-Kept")));
    }

    [Fact]
    public async Task ForwardAsync_RewritesHostToTheUpstreamAuthority()
    {
        var handler = new RecordingHandler();

        await ForwardAsync(handler, request => request.Headers["Host"] = "devconduit.corp:8091");

        Assert.Equal("upstream.test:9099", handler.Sent!.Headers.Host);
    }

    [Fact]
    public async Task ForwardAsync_DropsTheCookieHeaderByDefault()
    {
        var handler = new RecordingHandler();

        await ForwardAsync(
            handler,
            request => request.Headers["Cookie"] = "DevConduit.Auth=session; app=1");

        Assert.False(handler.Sent!.Headers.Contains("Cookie"));
    }

    [Fact]
    public async Task ForwardAsync_StripsDevConduitCookiesWhenForwardingIsTurnedOn()
    {
        var handler = new RecordingHandler();

        await ForwardAsync(
            handler,
            request => request.Headers["Cookie"] =
                "DevConduit.Auth=session; app=1; .AspNetCore.Antiforgery.X=t; .AspNetCore.Correlation.Y=c; .AspNetCore.OpenIdConnect.Nonce.Z=n",
            options => options.Route.ForwardCookies = true);

        Assert.Equal("app=1", Assert.Single(handler.Sent!.Headers.GetValues("Cookie")));
    }

    [Fact]
    public async Task ForwardAsync_RelaysTheUpstreamStatusAndBody()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created, "created payload");

        var context = await ForwardAsync(handler);

        Assert.Equal(201, context.Response.StatusCode);
        Assert.Equal("created payload", ReadResponseBody(context));
    }

    [Fact]
    public async Task ForwardAsync_ReportsAnUnresolvableUpstreamAsADnsFailure()
    {
        var handler = new RecordingHandler((_, _) =>
            throw new HttpRequestException(HttpRequestError.NameResolutionError, "no such host"));

        var context = await ForwardAsync(handler);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.Equal(nameof(ProxyErrorKind.Dns), ReadFailure(context).ErrorKind);
    }

    [Fact]
    public async Task ForwardAsync_ReportsAStalledUpstreamAsATimeoutRatherThanACancellation()
    {
        var handler = new RecordingHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var context = await ForwardAsync(handler, configureOptions: options => options.Route.TimeoutSeconds = 1);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.Equal(nameof(ProxyErrorKind.Timeout), ReadFailure(context).ErrorKind);
    }

    [Fact]
    public async Task ForwardAsync_AnswersABlockedDestinationWith400AndRecordsItAsBlocked()
    {
        var blocked = new EgressBlockedException("meta", IPAddress.Parse("169.254.169.254"));
        var handler = new RecordingHandler((_, _) => throw new HttpRequestException("x", blocked));
        var store = new RecordingRoutedTrafficStore();
        var recorder = new RoutedTrafficRecorder(
            store,
            new SecretHeaderPolicy(TestOptions.Create()),
            NullLogger<RoutedTrafficRecorder>.Instance);

        var context = await ForwardAsync(handler, recorder: recorder);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var failure = ReadFailure(context);
        Assert.Equal(nameof(ProxyErrorKind.Blocked), failure.ErrorKind);
        Assert.Equal(blocked.Message, failure.Error);

        var entry = await DrainSingleAsync(recorder, store);
        Assert.Equal(nameof(ProxyErrorKind.Blocked), entry.ErrorKind);
        Assert.Equal(blocked.Message, entry.Error);
        Assert.Null(entry.StatusCode);
    }

    [Fact]
    public async Task ForwardAsync_AnswersAConnectTimeTargetRefusalWith403()
    {
        var refused = new TargetNotAllowedException("upstream.test", 9099, [IPAddress.Parse("203.0.113.5")]);
        var handler = new RecordingHandler((_, _) => throw new HttpRequestException("x", refused));
        var store = new RecordingRoutedTrafficStore();
        var recorder = new RoutedTrafficRecorder(
            store,
            new SecretHeaderPolicy(TestOptions.Create()),
            NullLogger<RoutedTrafficRecorder>.Instance);

        var context = await ForwardAsync(handler, recorder: recorder);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        var failure = ReadFailure(context);
        Assert.Equal(TargetAllowList.ErrorKind, failure.ErrorKind);
        Assert.Equal(refused.Message, failure.Error);
        Assert.Equal(UpstreamOrigin, failure.Target);

        var entry = await DrainSingleAsync(recorder, store);
        Assert.Equal(nameof(ProxyErrorKind.TargetNotAllowed), entry.ErrorKind);
    }

    /// <summary>Runs the recorder's background loop just long enough to store the one queued exchange.</summary>
    private static async Task<RoutedEntry> DrainSingleAsync(RoutedTrafficRecorder recorder, RecordingRoutedTrafficStore store)
    {
        await recorder.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (store.Entries)
                {
                    if (store.Entries.Count > 0)
                    {
                        return Assert.Single(store.Entries);
                    }
                }

                await Task.Delay(10);
            }

            throw new TimeoutException("The recorder did not store the exchange.");
        }
        finally
        {
            await recorder.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<DefaultHttpContext> ForwardAsync(
        RecordingHandler handler,
        Action<HttpRequest>? configureRequest = null,
        Action<DevConduitOptions>? configureOptions = null,
        RoutedTrafficRecorder? recorder = null)
    {
        var options = TestOptions.Create(configureOptions);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = new PathString("/api/cases");
        context.Response.Body = new MemoryStream();
        configureRequest?.Invoke(context.Request);

        var forwarder = new RouteForwarder(
            new StubHttpClientFactory(handler),
            recorder ?? new RoutedTrafficRecorder(
                new RecordingRoutedTrafficStore(),
                new SecretHeaderPolicy(options),
                NullLogger<RoutedTrafficRecorder>.Instance),
            new UpstreamRouteEgress(TargetAllowList.Unrestricted),
            options,
            NullLogger<RouteForwarder>.Instance);

        var target = new RouteTarget(new Uri(UpstreamOrigin), null, context.Request.Path);
        await forwarder.ForwardAsync(context, target, "test-user");
        return context;
    }

    private static string ReadResponseBody(HttpContext context)
    {
        var body = (MemoryStream)context.Response.Body;
        return Encoding.UTF8.GetString(body.ToArray());
    }

    private static RouteFailure ReadFailure(HttpContext context) =>
        JsonSerializer.Deserialize<RouteFailure>(
            ReadResponseBody(context),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
}
