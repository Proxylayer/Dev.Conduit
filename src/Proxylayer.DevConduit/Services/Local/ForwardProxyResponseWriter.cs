using System.Buffers;
using System.Text;

namespace Proxylayer.DevConduit.Services.Local;

/// <summary>
/// Writes HTTP/1.1 responses back onto a raw client socket. The framing decision is made
/// here and nowhere else: a response whose length is known can be relayed on a connection
/// that stays open, and anything else is relayed with <c>Connection: close</c> so the end of
/// the body is unambiguous.
/// </summary>
public static class ForwardProxyResponseWriter
{
    private const int CopyBufferSize = 64 * 1024;

    /// <summary>Sends a self-contained plain-text response and closes the connection.</summary>
    public static async Task WriteErrorAsync(
        Stream client,
        int statusCode,
        string reason,
        string body,
        CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(Sanitize(reason)).Append("\r\n")
            .Append("Content-Type: text/plain; charset=utf-8\r\n")
            .Append("Content-Length: ").Append(payload.Length).Append("\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();

        await client.WriteAsync(Encoding.Latin1.GetBytes(head), cancellationToken).ConfigureAwait(false);
        await client.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await client.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Relays an upstream response. Returns true when the connection may carry another
    /// request, which is only ever the case for a length-delimited body.
    /// </summary>
    public static async Task<bool> RelayAsync(
        Stream client,
        HttpResponseMessage response,
        bool bodyExpected,
        bool keepAlive,
        CancellationToken cancellationToken)
    {
        var contentLength = response.Content.Headers.ContentLength;
        var canKeepAlive = keepAlive && contentLength is not null;

        var head = new StringBuilder()
            .Append("HTTP/1.1 ")
            .Append((int)response.StatusCode)
            .Append(' ')
            .Append(Sanitize(response.ReasonPhrase ?? response.StatusCode.ToString()))
            .Append("\r\n");

        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (IsConnectionScoped(header.Key))
            {
                continue;
            }

            foreach (var value in header.Value)
            {
                head.Append(header.Key).Append(": ").Append(Sanitize(value)).Append("\r\n");
            }
        }

        if (contentLength is null && bodyExpected)
        {
            // No declared length and no chunked framing of our own: closing is the only
            // honest way to say where the body ends.
            head.Append("Connection: close\r\n");
        }
        else
        {
            head.Append("Connection: ").Append(canKeepAlive ? "keep-alive" : "close").Append("\r\n");
        }

        head.Append("\r\n");
        await client.WriteAsync(Encoding.Latin1.GetBytes(head.ToString()), cancellationToken).ConfigureAwait(false);

        if (bodyExpected)
        {
            await CopyAsync(response, client, cancellationToken).ConfigureAwait(false);
        }

        await client.FlushAsync(cancellationToken).ConfigureAwait(false);
        return canKeepAlive;
    }

    private static async Task CopyAsync(HttpResponseMessage response, Stream client, CancellationToken cancellationToken)
    {
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            int read;
            while ((read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await client.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Headers describing the upstream connection rather than the message. Content-Length
    /// is kept because it frames the body the client is about to read.
    /// </summary>
    private static bool IsConnectionScoped(string name) =>
        RouteHeaderPolicy.HopByHop.Contains(name)
        || string.Equals(name, "Proxy-Connection", StringComparison.OrdinalIgnoreCase);

    /// <summary>Strips anything that could inject an extra header line.</summary>
    private static string Sanitize(string value) => value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
