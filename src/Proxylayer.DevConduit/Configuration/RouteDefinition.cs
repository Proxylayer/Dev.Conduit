namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// One entry of <c>DevConduit:Routes</c>: a name usable as the <c>/route/{name}/...</c> alias
/// segment, the upstream origin it stands for and, in local mode, the remote that carries it.
/// </summary>
/// <param name="Name">The route's name, the key under <c>DevConduit:Routes</c>.</param>
/// <param name="Target">The upstream origin as configured. Validated by <c>RouteAliasTable</c>.</param>
/// <param name="Remote">The remote named by the entry, or null to use the default remote.</param>
public sealed record RouteDefinition(string Name, string Target, string? Remote);
