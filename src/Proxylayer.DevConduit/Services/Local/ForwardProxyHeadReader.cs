using System.Buffers;
using System.Text;

namespace Proxylayer.DevConduit.Services.Local;

/// <summary>What one attempt to read a request head produced.</summary>
/// <param name="Head">The parsed head, or null when the connection closed or the head was bad.</param>
/// <param name="Leftover">Bytes read past the header block; the start of the body.</param>
/// <param name="Error">A reason to answer 400 with, or null.</param>
/// <param name="Closed">True when the peer closed the connection before sending anything.</param>
public sealed record ForwardProxyHeadResult(
    ForwardProxyRequestHead? Head,
    byte[] Leftover,
    string? Error,
    bool Closed);

/// <summary>
/// Reads the request line and header block off a raw socket. Both the request line and the
/// whole block are capped, and the caller supplies a cancellation token carrying a deadline,
/// so a peer that opens a connection and dribbles bytes cannot hold a slot open.
/// </summary>
public static class ForwardProxyHeadReader
{
    /// <summary>Largest accepted request line, in bytes.</summary>
    public const int MaxRequestLineBytes = 8 * 1024;

    /// <summary>Largest accepted request line plus header block, in bytes.</summary>
    public const int MaxHeadBytes = 64 * 1024;

    private static readonly byte[] Terminator = "\r\n\r\n"u8.ToArray();

    public static async Task<ForwardProxyHeadResult> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxHeadBytes);
        try
        {
            var filled = 0;

            while (true)
            {
                var read = await stream
                    .ReadAsync(buffer.AsMemory(filled, MaxHeadBytes - filled), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    return filled == 0
                        ? new ForwardProxyHeadResult(null, [], null, Closed: true)
                        : new ForwardProxyHeadResult(null, [], "The connection closed inside the header block.", false);
                }

                filled += read;

                var end = buffer.AsSpan(0, filled).IndexOf(Terminator);
                if (end >= 0)
                {
                    return Parse(buffer, filled, end);
                }

                if (filled >= MaxHeadBytes)
                {
                    return new ForwardProxyHeadResult(
                        null, [], $"The request head exceeded {MaxHeadBytes} bytes.", false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ForwardProxyHeadResult Parse(byte[] buffer, int filled, int end)
    {
        var text = Encoding.Latin1.GetString(buffer, 0, end);
        var bodyStart = end + Terminator.Length;
        var leftover = buffer.AsSpan(bodyStart, filled - bodyStart).ToArray();

        var firstBreak = text.IndexOf("\r\n", StringComparison.Ordinal);
        var requestLineLength = firstBreak < 0 ? text.Length : firstBreak;
        if (requestLineLength > MaxRequestLineBytes)
        {
            return new ForwardProxyHeadResult(
                null, leftover, $"The request line exceeded {MaxRequestLineBytes} bytes.", false);
        }

        return ForwardProxyRequestHead.TryParse(text, out var head, out var error)
            ? new ForwardProxyHeadResult(head, leftover, null, false)
            : new ForwardProxyHeadResult(null, leftover, error, false);
    }
}
