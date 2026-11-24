using System.Net;

namespace Proxylayer.DevConduit.Tests.Support;

/// <summary>
/// A message handler that records the outbound request and answers from a supplied
/// delegate. No socket is opened and no port is bound.
/// </summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) =>
        _responder = responder;

    public RecordingHandler(HttpStatusCode status = HttpStatusCode.OK, string body = "upstream body")
        : this((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }))
    {
    }

    /// <summary>The request as it was about to leave, captured before the response is produced.</summary>
    public HttpRequestMessage? Sent { get; private set; }

    /// <summary>The outbound body, read while it is still readable.</summary>
    public string? SentBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Sent = request;
        if (request.Content is not null)
        {
            SentBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        return await _responder(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Hands every named client the same recording handler.</summary>
internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false)
    {
        // The forwarder owns the timeout; the client must not impose one of its own.
        Timeout = Timeout.InfiniteTimeSpan
    };
}
