using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Services;

/// <summary>The upstream a forwarded request resolved to, and what is left of its path.</summary>
public sealed record RouteTarget(Uri Origin, string? Alias, PathString RemainingPath);

/// <summary>Why a request could not be pointed at an upstream, and the status <c>/route</c> answers with.</summary>
public sealed record RouteResolutionFailure(string Kind, string Message, int Status = StatusCodes.Status400BadRequest);

/// <summary>
/// Works out where a <c>/route</c> call is going: a leading alias segment first,
/// then the X-DevConduit-Target header, then nowhere.
/// </summary>
public sealed class RouteTargetResolver
{
    public const string TargetHeader = DevConduitHeaderNames.Target;

    private readonly RouteAliasTable _aliases;
    private readonly RouteForwardingOptions _options;

    public RouteTargetResolver(RouteAliasTable aliases, IOptions<DevConduitOptions> options)
    {
        _aliases = aliases;
        _options = options.Value.Route;
    }

    public bool TryResolve(HttpRequest request, out RouteTarget target, out RouteResolutionFailure failure)
    {
        target = null!;
        failure = null!;

        var path = request.Path;
        var (firstSegment, remainder) = SplitFirstSegment(path);

        if (firstSegment.Length > 0 && _aliases.TryResolve(firstSegment, out var aliasOrigin))
        {
            target = new RouteTarget(aliasOrigin, firstSegment, remainder);
            return true;
        }

        var header = request.Headers[TargetHeader].ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            failure = new RouteResolutionFailure(
                "no_target",
                $"No upstream was named. Send a '{TargetHeader}: http://host:port' header, or start the path with a configured alias{DescribeAliases()}.");
            return false;
        }

        if (_options.AliasesOnly)
        {
            failure = new RouteResolutionFailure(
                "aliases_only",
                $"DevConduit:Route:AliasesOnly is true, so '{TargetHeader}' is refused. Use a configured alias{DescribeAliases()}.");
            return false;
        }

        if (!RouteOrigin.TryParse(header, out var origin, out var error))
        {
            failure = new RouteResolutionFailure("invalid_target", $"The '{TargetHeader}' header is not usable: {error}");
            return false;
        }

        target = new RouteTarget(origin, null, path);
        return true;
    }

    /// <summary>The absolute upstream URI: origin, then the caller's remaining path and query verbatim.</summary>
    public static Uri BuildUpstreamUri(RouteTarget target, QueryString query)
    {
        // The origin owns the scheme, host and port; the port is never repeated in the path.
        var origin = target.Origin.GetLeftPart(UriPartial.Authority);
        var path = target.RemainingPath.HasValue ? target.RemainingPath.ToUriComponent() : "/";
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return new Uri(origin + path + query.ToUriComponent(), UriKind.Absolute);
    }

    private string DescribeAliases() =>
        _aliases.Names.Count == 0
            ? " (none are configured)"
            : $" ({string.Join(", ", _aliases.Names)})";

    private static (string Segment, PathString Remainder) SplitFirstSegment(PathString path)
    {
        var value = path.HasValue ? path.Value! : string.Empty;
        if (value.Length <= 1)
        {
            return (string.Empty, path);
        }

        var separator = value.IndexOf('/', 1);
        var segment = separator < 0 ? value[1..] : value[1..separator];
        var remainder = separator < 0 ? PathString.Empty : new PathString(value[separator..]);
        return (Uri.UnescapeDataString(segment), remainder);
    }
}
