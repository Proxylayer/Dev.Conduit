using System.Text;

namespace Proxylayer.DevConduit.Services;

/// <summary>Classifies media types and resolves the charset used to decode a payload.</summary>
public static class HttpContentTypes
{
    private static readonly string[] TextualExact =
    [
        "application/json",
        "application/xml",
        "application/javascript",
        "application/x-javascript",
        "application/ecmascript",
        "application/x-www-form-urlencoded",
        "application/graphql"
    ];

    /// <summary>Headers that belong on the content rather than the request.</summary>
    public static readonly HashSet<string> ContentHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type",
        "Content-Encoding",
        "Content-Disposition",
        "Content-Language",
        "Content-Location",
        "Content-MD5",
        "Content-Range",
        "Expires",
        "Last-Modified",
        "Allow"
    };

    public static bool IsTextual(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return false;
        }

        var value = mediaType.Trim();
        var separator = value.IndexOf(';');
        if (separator >= 0)
        {
            value = value[..separator].Trim();
        }

        if (value.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith("+xml", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith("+yaml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TextualExact.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The encoding named by the charset parameter, or UTF-8 when absent or unknown.</summary>
    public static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim('"', ' '));
        }
        catch (ArgumentException)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
    }
}
