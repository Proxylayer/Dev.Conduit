namespace Proxylayer.DevConduit.Tray.Setup;

/// <summary>
/// One row of the routes grid: an entry of <c>DevConduit:Routes</c> as the merged
/// configuration holds it. A row the layers below define carries their values in
/// <see cref="Inherited"/>; it is written to the settings file only once it differs.
/// </summary>
public sealed class RouteRow
{
    public string Name { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;

    /// <summary>The server (remote) that carries the route; blank for the only server.</summary>
    public string Remote { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>The layer below that defines this route, or null for a route added in this file.</summary>
    public string? InheritedFrom { get; init; }

    /// <summary>The values the layers below give this route, or null for a route added in this file.</summary>
    public RouteRow? Inherited { get; init; }
}
