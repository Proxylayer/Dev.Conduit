using System.Diagnostics.CodeAnalysis;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// The configured alias-to-origin map, read from <c>DevConduit:Routes</c> and validated once
/// at startup. Each route's name is its alias and its target the origin; the remote a route
/// names matters only to local mode and is ignored here. Malformed entries are dropped and
/// reported through <see cref="Problems"/> rather than failing the host.
/// </summary>
public sealed class RouteAliasTable
{
    private readonly Dictionary<string, Uri> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _problems = [];

    public RouteAliasTable(RoutingConfiguration routing)
    {
        foreach (var (name, value, _) in routing.Routes)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                _problems.Add("An alias with an empty name was ignored.");
                continue;
            }

            var alias = name.Trim();
            if (alias.Contains('/') || alias.Contains('\\'))
            {
                _problems.Add($"Alias '{alias}' was ignored: an alias must be a single path segment.");
                continue;
            }

            if (!RouteOrigin.TryParse(value, out var origin, out var error))
            {
                _problems.Add($"Alias '{alias}' was ignored: {error}");
                continue;
            }

            _aliases[alias] = origin;
        }
    }

    /// <summary>Alias names in configuration order, for diagnostics.</summary>
    public IReadOnlyCollection<string> Names => _aliases.Keys;

    /// <summary>The origins of the usable aliases, in configuration order.</summary>
    public IReadOnlyCollection<Uri> Origins => _aliases.Values;

    /// <summary>Configuration entries that were rejected, with the reason.</summary>
    public IReadOnlyList<string> Problems => _problems;

    public bool TryResolve(string name, [NotNullWhen(true)] out Uri? origin) =>
        _aliases.TryGetValue(name, out origin);
}
