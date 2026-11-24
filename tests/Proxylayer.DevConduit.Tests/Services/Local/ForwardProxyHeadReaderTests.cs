using System.Text;
using Proxylayer.DevConduit.Services.Local;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Services.Local;

/// <summary>
/// Reading a request head off a raw stream. The peer controls the bytes and the framing,
/// so truncated, oversized and malformed input must end in a clean error rather than an
/// unhandled exception or a wait that never ends.
/// </summary>
public sealed class ForwardProxyHeadReaderTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ReadAsync_ParsesAWellFormedHeadAndKeepsTheBodyBytes()
    {
        var payload = Bytes("POST http://example.com/a HTTP/1.1\r\nHost: example.com\r\nContent-Length: 5\r\n\r\nhello");

        var result = await ReadAsync(payload);

        Assert.Null(result.Error);
        Assert.False(result.Closed);
        Assert.Equal("POST", result.Head!.Method);
        Assert.Equal("http://example.com/a", result.Head.Target);
        Assert.Equal(5, result.Head.ContentLength);
        Assert.Equal("hello", Encoding.Latin1.GetString(result.Leftover));
    }

    [Fact]
    public async Task ReadAsync_ParsesAConnectRequest()
    {
        var result = await ReadAsync(Bytes("CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n"));

        Assert.Null(result.Error);
        Assert.True(result.Head!.IsConnect);
        Assert.Empty(result.Leftover);
    }

    [Fact]
    public async Task ReadAsync_ReassemblesAHeadDeliveredOneByteAtATime()
    {
        var payload = Bytes("GET http://example.com/ HTTP/1.1\r\nHost: example.com\r\n\r\nxy");

        var result = await ReadAsync(payload, sliceSize: 1);

        Assert.Null(result.Error);
        Assert.Equal("example.com", result.Head!.First("Host"));

        // The reader stops at the terminator, so leftover holds only body bytes that
        // happened to arrive in the same read; the rest stays on the stream.
        Assert.Empty(result.Leftover);
    }

    [Fact]
    public async Task ReadAsync_ReportsAPeerThatClosedWithoutSendingAnything()
    {
        var result = await ReadAsync([]);

        Assert.True(result.Closed);
        Assert.Null(result.Head);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("GET http://example.com/ HTT")]
    [InlineData("GET http://example.com/ HTTP/1.1\r\n")]
    [InlineData("GET http://example.com/ HTTP/1.1\r\nHost: example.com\r\n")]
    [InlineData("GET http://example.com/ HTTP/1.1\r\nHost: example.com\r\n\r")]
    public async Task ReadAsync_ReportsAConnectionThatClosedInsideTheHeaderBlock(string partial)
    {
        var result = await ReadAsync(Bytes(partial));

        Assert.False(result.Closed);
        Assert.Null(result.Head);
        Assert.Equal("The connection closed inside the header block.", result.Error);
    }

    [Fact]
    public async Task ReadAsync_DoesNotAcceptBareLineFeedFraming()
    {
        // Only CRLFCRLF ends the head, so a peer using bare LF runs to end of stream.
        var result = await ReadAsync(Bytes("GET http://example.com/ HTTP/1.1\nHost: example.com\n\n"));

        Assert.Null(result.Head);
        Assert.Equal("The connection closed inside the header block.", result.Error);
    }

    [Fact]
    public async Task ReadAsync_RejectsAHeadBiggerThanTheCap()
    {
        var filler = new string('a', ForwardProxyHeadReader.MaxHeadBytes + 4_096);
        var payload = Bytes($"GET http://example.com/ HTTP/1.1\r\nX-Filler: {filler}\r\n\r\n");

        var result = await ReadAsync(payload);

        Assert.Null(result.Head);
        Assert.Equal($"The request head exceeded {ForwardProxyHeadReader.MaxHeadBytes} bytes.", result.Error);
    }

    [Fact]
    public async Task ReadAsync_RejectsARequestLineBiggerThanItsOwnCap()
    {
        // Comfortably inside the head cap, so only the request-line cap can reject it.
        var target = "http://example.com/" + new string('b', ForwardProxyHeadReader.MaxRequestLineBytes);
        var payload = Bytes($"GET {target} HTTP/1.1\r\nHost: example.com\r\n\r\n");

        var result = await ReadAsync(payload);

        Assert.Null(result.Head);
        Assert.Equal($"The request line exceeded {ForwardProxyHeadReader.MaxRequestLineBytes} bytes.", result.Error);
    }

    [Fact]
    public async Task ReadAsync_AcceptsARequestLineExactlyAtTheCap()
    {
        const string prefix = "GET http://example.com/";
        const string suffix = " HTTP/1.1";
        var padding = ForwardProxyHeadReader.MaxRequestLineBytes - prefix.Length - suffix.Length;
        var requestLine = prefix + new string('c', padding) + suffix;
        Assert.Equal(ForwardProxyHeadReader.MaxRequestLineBytes, requestLine.Length);

        var result = await ReadAsync(Bytes($"{requestLine}\r\nHost: example.com\r\n\r\n"));

        Assert.Null(result.Error);
        Assert.Equal("GET", result.Head!.Method);
    }

    [Fact]
    public async Task ReadAsync_SurfacesAParseFailureAsAnError()
    {
        var result = await ReadAsync(Bytes("NONSENSE\r\n\r\n"));

        Assert.Null(result.Head);
        Assert.Equal("The request line is not 'METHOD target HTTP/1.x'.", result.Error);
    }

    [Fact]
    public async Task ReadAsync_SurfacesBinaryGarbageAsAnError()
    {
        var payload = new byte[] { 0x16, 0x03, 0x01, 0x00, 0xF4, 0x01, (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };

        var result = await ReadAsync(payload);

        Assert.Null(result.Head);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ReadAsync_ObservesTheCancellationTokenWhenThePeerStalls()
    {
        // A peer that opens a connection and then dribbles nothing must not hold a slot.
        await using var stream = new ScriptedStream(Bytes("GET http://example.com/ HTTP/1.1\r\n"), blockAtEnd: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        var read = ForwardProxyHeadReader.ReadAsync(stream, deadline.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => read.WaitAsync(Patience, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_NeverThrowsOnArbitraryBytes()
    {
        var random = new Random(20250918);

        for (var iteration = 0; iteration < 500; iteration++)
        {
            var payload = new byte[random.Next(0, 512)];
            random.NextBytes(payload);

            var result = await ReadAsync(payload);

            // Exactly one of the three outcomes, and never an exception.
            var outcomes = (result.Head is not null ? 1 : 0) + (result.Error is not null ? 1 : 0) + (result.Closed ? 1 : 0);
            Assert.Equal(1, outcomes);
        }
    }

    [Fact]
    public async Task ReadAsync_NeverThrowsOnTruncationsOfAValidHead()
    {
        var full = Bytes("POST http://example.com/a?b=1 HTTP/1.1\r\nHost: example.com\r\nContent-Length: 3\r\n\r\nabc");

        for (var length = 0; length <= full.Length; length++)
        {
            var result = await ReadAsync(full[..length]);

            var outcomes = (result.Head is not null ? 1 : 0) + (result.Error is not null ? 1 : 0) + (result.Closed ? 1 : 0);
            Assert.Equal(1, outcomes);
        }
    }

    private static async Task<ForwardProxyHeadResult> ReadAsync(byte[] payload, int sliceSize = int.MaxValue)
    {
        await using var stream = new ScriptedStream(payload, sliceSize);
        using var deadline = new CancellationTokenSource(Patience);
        return await ForwardProxyHeadReader.ReadAsync(stream, deadline.Token).WaitAsync(Patience);
    }

    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);
}
