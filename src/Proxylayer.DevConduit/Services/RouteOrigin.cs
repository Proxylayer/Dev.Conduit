using System.Diagnostics.CodeAnalysis;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Parses and validates an upstream origin. An origin is a scheme and authority and
/// nothing else: the path, query and fragment come from the caller's own request.
/// </summary>
public static class RouteOrigin
{
    public static bool TryParse(string? value, [NotNullWhen(true)] out Uri? origin, out string error)
    {
        origin = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "The target origin is empty.";
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed))
        {
            error = $"'{value}' is not an absolute URI. Use the form http://host:port.";
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = $"The scheme '{parsed.Scheme}' is not supported. Use http or https.";
            return false;
        }

        if (parsed.AbsolutePath.Length > 1)
        {
            error = $"'{value}' carries a path. The target must be an origin only, for example {parsed.GetLeftPart(UriPartial.Authority)}.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            error = $"'{value}' carries a query or fragment. The target must be an origin only.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "The target must not embed credentials. Use X-DevConduit-Upstream-Authorization instead.";
            return false;
        }

        origin = new Uri(parsed.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
        return true;
    }
}
