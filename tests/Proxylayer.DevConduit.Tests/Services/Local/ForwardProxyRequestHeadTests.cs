using Proxylayer.DevConduit.Services.Local;

namespace Proxylayer.DevConduit.Tests.Services.Local;

/// <summary>
/// Parsing of the hand-rolled request line and header block. Everything here is a string
/// the peer controls, so the emphasis is on what must be refused.
/// </summary>
public sealed class ForwardProxyRequestHeadTests
{
    [Fact]
    public void TryParse_AcceptsAbsoluteFormRequestLine()
    {
        var parsed = ForwardProxyRequestHead.TryParse(
            "GET http://example.com/a/b?c=1 HTTP/1.1\r\nHost: example.com\r\nAccept: */*",
            out var head,
            out var error);

        Assert.True(parsed, error);
        Assert.Equal("GET", head!.Method);
        Assert.Equal("http://example.com/a/b?c=1", head.Target);
        Assert.Equal("HTTP/1.1", head.Version);
        Assert.Equal(2, head.Headers.Count);
        Assert.Equal("example.com", head.First("host"));
        Assert.False(head.IsConnect);
    }

    [Fact]
    public void TryParse_AcceptsConnectAuthority()
    {
        var parsed = ForwardProxyRequestHead.TryParse(
            "CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443",
            out var head,
            out var error);

        Assert.True(parsed, error);
        Assert.True(head!.IsConnect);
        Assert.Equal("example.com:443", head.Target);
    }

    [Fact]
    public void TryParse_KeepsRelativeFormTargetVerbatim()
    {
        // The parser does not judge the target's form; the connection handler is what
        // requires an absolute URI. This pins the boundary between the two.
        var parsed = ForwardProxyRequestHead.TryParse("GET /a/b HTTP/1.1\r\nHost: example.com", out var head, out _);

        Assert.True(parsed);
        Assert.Equal("/a/b", head!.Target);
        Assert.False(Uri.TryCreate(head.Target, UriKind.Absolute, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\nHost: example.com")]
    public void TryParse_RejectsEmptyRequestLine(string text)
    {
        Assert.False(ForwardProxyRequestHead.TryParse(text, out _, out var error));
        Assert.Equal("The request line is empty.", error);
    }

    [Theory]
    [InlineData("GARBAGE")]
    [InlineData("GET")]
    [InlineData("GET http://example.com/")]
    [InlineData("GET http://example.com/ HTTP/1.1 extra")]
    public void TryParse_RejectsRequestLinesThatAreNotThreeTokens(string requestLine)
    {
        Assert.False(ForwardProxyRequestHead.TryParse(requestLine, out _, out var error));
        Assert.Equal("The request line is not 'METHOD target HTTP/1.x'.", error);
    }

    [Theory]
    [InlineData("GET http://example.com/ FTP/1.1")]
    [InlineData("GET http://example.com/ 1.1")]
    [InlineData("GET http://example.com/ http/1.1")]
    [InlineData("\0\0\0 \0 \0")]
    public void TryParse_RejectsNonHttpVersions(string requestLine)
    {
        Assert.False(ForwardProxyRequestHead.TryParse(requestLine, out _, out var error));
        Assert.Contains("is not an HTTP version", error);
    }

    [Fact]
    public void TryParse_CollapsesRunsOfSpacesInTheRequestLine()
    {
        var parsed = ForwardProxyRequestHead.TryParse("GET   http://example.com/   HTTP/1.1", out var head, out _);

        Assert.True(parsed);
        Assert.Equal("http://example.com/", head!.Target);
    }

    [Fact]
    public void TryParse_TrimsHeaderNamesAndValues()
    {
        var parsed = ForwardProxyRequestHead.TryParse(
            "GET http://example.com/ HTTP/1.1\r\nX-Spaced  :   value with spaces  ",
            out var head,
            out _);

        Assert.True(parsed);
        var header = Assert.Single(head!.Headers);
        Assert.Equal("X-Spaced", header.Name);
        Assert.Equal("value with spaces", header.Value);
    }

    [Fact]
    public void TryParse_AcceptsAnEmptyHeaderValue()
    {
        var parsed = ForwardProxyRequestHead.TryParse("GET http://example.com/ HTTP/1.1\r\nX-Empty:", out var head, out _);

        Assert.True(parsed);
        Assert.Equal(string.Empty, head!.First("X-Empty"));
    }

    [Theory]
    [InlineData(": orphaned")]
    [InlineData("NoColonAtAll")]
    [InlineData(":")]
    public void TryParse_RejectsHeaderLinesWithoutAName(string headerLine)
    {
        Assert.False(
            ForwardProxyRequestHead.TryParse($"GET http://example.com/ HTTP/1.1\r\n{headerLine}", out _, out var error));
        Assert.Equal("A header line carries no name.", error);
    }

    [Theory]
    [InlineData(" folded")]
    [InlineData("\tfolded")]
    public void TryParse_RejectsObsoleteLineFolding(string continuation)
    {
        Assert.False(
            ForwardProxyRequestHead.TryParse(
                $"GET http://example.com/ HTTP/1.1\r\nX-Name: value\r\n{continuation}",
                out _,
                out var error));
        Assert.Equal("Obsolete line folding in the header block is not accepted.", error);
    }

    [Fact]
    public void TryParse_KeepsDuplicateHeadersAndReturnsTheFirst()
    {
        var parsed = ForwardProxyRequestHead.TryParse(
            "GET http://example.com/ HTTP/1.1\r\nX-Dup: one\r\nX-Dup: two",
            out var head,
            out _);

        Assert.True(parsed);
        Assert.Equal(2, head!.Headers.Count);
        Assert.Equal("one", head.First("X-Dup"));
    }

    [Fact]
    public void First_ReturnsNullForAnAbsentHeader() =>
        Assert.Null(Parse("GET http://example.com/ HTTP/1.1").First("X-Missing"));

    [Fact]
    public void TryParse_AcceptsHeadersUpToTheCap()
    {
        var text = BuildWithHeaders(ForwardProxyRequestHead.MaxHeaderCount);

        Assert.True(ForwardProxyRequestHead.TryParse(text, out var head, out var error), error);
        Assert.Equal(ForwardProxyRequestHead.MaxHeaderCount, head!.Headers.Count);
    }

    [Fact]
    public void TryParse_RejectsOneHeaderBeyondTheCap()
    {
        var text = BuildWithHeaders(ForwardProxyRequestHead.MaxHeaderCount + 1);

        Assert.False(ForwardProxyRequestHead.TryParse(text, out _, out var error));
        Assert.Contains("request headers were sent", error);
    }

    [Theory]
    [InlineData("42", 42L)]
    [InlineData("0", 0L)]
    public void ContentLength_ReadsADeclaredLength(string value, long expected) =>
        Assert.Equal(expected, Parse($"POST http://example.com/ HTTP/1.1\r\nContent-Length: {value}").ContentLength);

    [Theory]
    [InlineData("-1")]
    [InlineData("nonsense")]
    [InlineData("")]
    [InlineData("9999999999999999999999")]
    public void ContentLength_IsNullWhenTheValueIsUnusable(string value) =>
        Assert.Null(Parse($"POST http://example.com/ HTTP/1.1\r\nContent-Length: {value}").ContentLength);

    [Fact]
    public void ContentLength_IsNullWhenNoneWasDeclared() =>
        Assert.Null(Parse("POST http://example.com/ HTTP/1.1").ContentLength);

    [Fact]
    public void IsChunked_FollowsTheTransferEncodingHeader()
    {
        Assert.True(Parse("POST http://example.com/ HTTP/1.1\r\nTransfer-Encoding: chunked").IsChunked);
        Assert.False(Parse("POST http://example.com/ HTTP/1.1").IsChunked);
        Assert.False(Parse("POST http://example.com/ HTTP/1.1\r\nTransfer-Encoding:").IsChunked);
    }

    [Theory]
    [InlineData("GET http://example.com/ HTTP/1.1", false)]
    [InlineData("GET http://example.com/ HTTP/1.1\r\nConnection: close", true)]
    [InlineData("GET http://example.com/ HTTP/1.1\r\nConnection: Keep-Alive, Close", true)]
    [InlineData("GET http://example.com/ HTTP/1.0", true)]
    [InlineData("GET http://example.com/ HTTP/1.0\r\nConnection: keep-alive", false)]
    [InlineData("GET http://example.com/ HTTP/1.1\r\nProxy-Connection: close", true)]
    public void WantsClose_FollowsTheConnectionHeaders(string text, bool expected) =>
        Assert.Equal(expected, Parse(text).WantsClose);

    [Fact]
    public void IsHead_IsCaseInsensitive()
    {
        Assert.True(Parse("head http://example.com/ HTTP/1.1").IsHead);
        Assert.False(Parse("GET http://example.com/ HTTP/1.1").IsHead);
    }

    private static ForwardProxyRequestHead Parse(string text)
    {
        Assert.True(ForwardProxyRequestHead.TryParse(text, out var head, out var error), error);
        return head!;
    }

    private static string BuildWithHeaders(int count)
    {
        var lines = new List<string> { "GET http://example.com/ HTTP/1.1" };
        for (var index = 0; index < count; index++)
        {
            lines.Add($"X-Header-{index}: {index}");
        }

        return string.Join("\r\n", lines);
    }
}
