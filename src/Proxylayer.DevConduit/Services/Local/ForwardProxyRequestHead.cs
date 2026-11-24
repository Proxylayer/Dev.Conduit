using System.Diagnostics.CodeAnalysis;

namespace Proxylayer.DevConduit.Services.Local;

/// <summary>One header line as the client wrote it.</summary>
public sealed record ForwardProxyHeader(string Name, string Value);

/// <summary>
/// The request line and header block of one forward-proxy request. A forward proxy sees
/// request forms Kestrel will not accept — an absolute URI as the target, or the
/// <c>CONNECT</c> verb — which is why this is parsed by hand.
/// </summary>
public sealed record ForwardProxyRequestHead(
    string Method,
    string Target,
    string Version,
    IReadOnlyList<ForwardProxyHeader> Headers)
{
    /// <summary>Headers beyond this count are treated as a malformed request.</summary>
    public const int MaxHeaderCount = 128;

    public bool IsConnect => string.Equals(Method, "CONNECT", StringComparison.OrdinalIgnoreCase);

    public bool IsHead => string.Equals(Method, "HEAD", StringComparison.OrdinalIgnoreCase);

    /// <summary>The first value of a header, or null.</summary>
    public string? First(string name) =>
        Headers.FirstOrDefault(header => string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>The declared body length, or null when none was declared.</summary>
    public long? ContentLength =>
        long.TryParse(First("Content-Length"), out var length) && length >= 0 ? length : null;

    /// <summary>True when the body is chunked rather than length-delimited.</summary>
    public bool IsChunked => !string.IsNullOrEmpty(First("Transfer-Encoding"));

    /// <summary>True when the connection must be closed after this exchange.</summary>
    public bool WantsClose
    {
        get
        {
            var connection = First("Connection") ?? First("Proxy-Connection") ?? string.Empty;

            if (connection.Contains("close", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // HTTP/1.0 defaults to closing unless the client asks otherwise.
            return Version.EndsWith("1.0", StringComparison.Ordinal)
                   && !connection.Contains("keep-alive", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Parses a decoded header block. The trailing blank line must already be removed.</summary>
    public static bool TryParse(string text, [NotNullWhen(true)] out ForwardProxyRequestHead? head, out string error)
    {
        head = null;
        error = string.Empty;

        var lines = text.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0 || lines[0].Length == 0)
        {
            error = "The request line is empty.";
            return false;
        }

        var parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            error = "The request line is not 'METHOD target HTTP/1.x'.";
            return false;
        }

        if (!parts[2].StartsWith("HTTP/", StringComparison.Ordinal))
        {
            error = $"'{parts[2]}' is not an HTTP version.";
            return false;
        }

        var headers = new List<ForwardProxyHeader>();
        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] is ' ' or '\t')
            {
                error = "Obsolete line folding in the header block is not accepted.";
                return false;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                error = "A header line carries no name.";
                return false;
            }

            if (headers.Count == MaxHeaderCount)
            {
                error = $"More than {MaxHeaderCount} request headers were sent.";
                return false;
            }

            headers.Add(new ForwardProxyHeader(line[..separator].Trim(), line[(separator + 1)..].Trim()));
        }

        head = new ForwardProxyRequestHead(parts[0], parts[1], parts[2], headers);
        return true;
    }
}
