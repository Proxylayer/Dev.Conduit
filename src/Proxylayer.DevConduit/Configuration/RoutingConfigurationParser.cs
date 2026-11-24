namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// Reads <c>DevConduit:Remotes</c>, <c>DevConduit:Routes</c> and <c>DevConduit:DefaultRemote</c>.
/// Every remote and route may be written as a plain string or as an object, which the
/// configuration binder cannot express, so the sections are read by hand here: a section's
/// own value is the string form, its children the object form. An entry whose <c>Enabled</c>
/// is <c>false</c> is left out as if it were absent: a settings file layered over
/// appsettings.json cannot delete a key the lower file defines, only switch it off.
/// </summary>
public static class RoutingConfigurationParser
{
    public const string RemotesKey = "Remotes";
    public const string RoutesKey = "Routes";
    public const string DefaultRemoteKey = "DefaultRemote";
    public const string EnabledKey = "Enabled";

    private const string Example = "\"Remotes\": { \"uat\": \"https://devconduit.corp:8091\" }";

    /// <summary>
    /// Parses the routing settings. Pure: reads only the two sections passed in.
    /// </summary>
    /// <param name="devConduitSection">The <c>DevConduit</c> section.</param>
    /// <param name="azureAd">The <c>AzureAd</c> section a remote's identity defaults to.</param>
    public static RoutingConfiguration Parse(IConfiguration devConduitSection, IConfiguration azureAd)
    {
        var problems = new List<string>();
        var azureTenantId = Clean(azureAd["TenantId"]);
        var azureClientId = Clean(azureAd["ClientId"]);

        var remotes = devConduitSection.GetSection(RemotesKey)
            .GetChildren()
            .Where(IsEnabled)
            .Select(section => ReadRemote(section, azureTenantId, azureClientId, problems))
            .ToList();

        var routes = devConduitSection.GetSection(RoutesKey)
            .GetChildren()
            .Where(IsEnabled)
            .Select(ReadRoute)
            .ToList();

        if (remotes.Count == 0)
        {
            problems.Insert(
                0,
                $"DevConduit:{RemotesKey} is required in local mode. Name at least one remote Dev.Conduit instance "
                + $"and its base address, for example {Example}.");
        }

        var configuredDefault = Clean(devConduitSection[DefaultRemoteKey]);
        var defaultRemote = ResolveDefault(configuredDefault, remotes, problems);

        CheckRouteRemotes(routes, remotes, configuredDefault, defaultRemote, problems);

        return new RoutingConfiguration(remotes, routes, defaultRemote, problems);
    }

    private static RemoteDefinition ReadRemote(
        IConfigurationSection section,
        string azureTenantId,
        string azureClientId,
        List<string> problems)
    {
        var name = section.Key.Trim();
        var endpoint = FirstNonBlank(section["Endpoint"], section.Value);

        var tenantId = Clean(section["TenantId"]);
        var inheritsTenantId = tenantId.Length == 0;
        if (inheritsTenantId)
        {
            tenantId = azureTenantId;
        }

        var clientId = Clean(section["ClientId"]);
        var inheritsClientId = clientId.Length == 0;
        if (inheritsClientId)
        {
            clientId = azureClientId;
        }

        var scopes = ReadList(section.GetSection("Scopes"));
        if (scopes.Count == 0)
        {
            scopes = [$"api://{clientId}/{RemoteDefinition.DefaultScopeName}"];
        }

        var baseAddress = TryResolveBaseAddress(name, endpoint, out var address, out var error) ? address : null;
        if (baseAddress is null)
        {
            problems.Add(error);
        }

        return new RemoteDefinition(
            name,
            endpoint,
            baseAddress,
            new RemoteIdentity(tenantId, clientId),
            inheritsTenantId,
            inheritsClientId,
            scopes,
            ReadList(section.GetSection("Targets")));
    }

    private static RouteDefinition ReadRoute(IConfigurationSection section)
    {
        var remote = Clean(section["Remote"]);
        return new RouteDefinition(
            section.Key.Trim(),
            FirstNonBlank(section["Target"], section.Value),
            remote.Length == 0 ? null : remote);
    }

    /// <summary>The remote's origin with a trailing slash, or the reason it cannot be used.</summary>
    private static bool TryResolveBaseAddress(string name, string endpoint, out Uri? address, out string error)
    {
        address = null;
        error = string.Empty;

        if (endpoint.Length == 0)
        {
            error = $"DevConduit:{RemotesKey}:{name} has no endpoint. Give it the base address of the remote Dev.Conduit "
                    + $"instance, either as the value itself (\"{name}\": \"https://devconduit.corp:8091\") or as Endpoint.";
            return false;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            error = $"DevConduit:{RemotesKey}:{name} endpoint ('{endpoint}') is not an absolute http or https URI.";
            return false;
        }

        address = new Uri(parsed.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        return true;
    }

    private static RemoteDefinition? ResolveDefault(
        string configuredDefault,
        IReadOnlyList<RemoteDefinition> remotes,
        List<string> problems)
    {
        if (configuredDefault.Length == 0)
        {
            return remotes.Count == 1 ? remotes[0] : null;
        }

        var match = Find(remotes, configuredDefault);
        if (match is null && remotes.Count > 0)
        {
            problems.Add(
                $"DevConduit:{DefaultRemoteKey} ('{configuredDefault}') names no remote under DevConduit:{RemotesKey}. "
                + $"Configured remotes: {Names(remotes)}.");
        }

        return match;
    }

    private static void CheckRouteRemotes(
        IReadOnlyList<RouteDefinition> routes,
        IReadOnlyList<RemoteDefinition> remotes,
        string configuredDefault,
        RemoteDefinition? defaultRemote,
        List<string> problems)
    {
        if (remotes.Count == 0)
        {
            // The missing remotes are already reported; every route would repeat it.
            return;
        }

        var unassigned = new List<string>();
        foreach (var route in routes)
        {
            if (route.Remote is { } remoteName)
            {
                if (Find(remotes, remoteName) is null)
                {
                    problems.Add(
                        $"DevConduit:{RoutesKey}:{route.Name} names the remote '{remoteName}', which is not under "
                        + $"DevConduit:{RemotesKey}. Configured remotes: {Names(remotes)}.");
                }
            }
            else if (defaultRemote is null && configuredDefault.Length == 0)
            {
                // An unknown DefaultRemote is reported once above rather than once per route.
                unassigned.Add(route.Name);
            }
        }

        if (unassigned.Count > 0)
        {
            problems.Add(
                $"The route{(unassigned.Count == 1 ? string.Empty : "s")} {string.Join(", ", unassigned)} under "
                + $"DevConduit:{RoutesKey} {(unassigned.Count == 1 ? "names" : "name")} no Remote, and with {remotes.Count} remotes "
                + $"configured ({Names(remotes)}) and no DevConduit:{DefaultRemoteKey} there is no remote to send them to. "
                + $"Set DevConduit:{DefaultRemoteKey}, or give each route a Remote.");
        }
    }

    /// <summary>
    /// False only for an entry whose <c>Enabled</c> is <c>false</c>. A missing or unreadable
    /// value leaves the entry in, so a typo can never silently drop a remote or route.
    /// </summary>
    public static bool IsEnabled(IConfigurationSection entry) =>
        !bool.TryParse(Clean(entry[EnabledKey]), out var enabled) || enabled;

    private static RemoteDefinition? Find(IEnumerable<RemoteDefinition> remotes, string name) =>
        remotes.FirstOrDefault(remote => string.Equals(remote.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string Names(IEnumerable<RemoteDefinition> remotes) =>
        string.Join(", ", remotes.Select(remote => remote.Name));

    /// <summary>
    /// A list setting: its children, or the section's own value when it was written as a
    /// single string. Blank entries are dropped and the rest trimmed.
    /// </summary>
    private static IReadOnlyList<string> ReadList(IConfigurationSection section)
    {
        var values = section.GetChildren()
            .Select(child => Clean(child.Value))
            .Where(value => value.Length > 0)
            .ToList();

        if (values.Count == 0 && Clean(section.Value) is { Length: > 0 } single)
        {
            values.Add(single);
        }

        return values;
    }

    private static string FirstNonBlank(string? preferred, string? fallback) =>
        Clean(preferred) is { Length: > 0 } value ? value : Clean(fallback);

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
}
