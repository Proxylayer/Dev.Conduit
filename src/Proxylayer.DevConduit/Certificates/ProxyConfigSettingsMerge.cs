using System.Text.Json.Nodes;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>The settings an import writes, and what it noticed on the way.</summary>
/// <param name="Changes">The change to the machine settings file: whole <c>Remotes</c> and <c>Routes</c> sections, and the AzureAd ids when they were blank.</param>
/// <param name="ImportedRoutes">The route names this import now owns, to replace on the next import from the same server.</param>
/// <param name="Notes">Lines for the summary: routes skipped because another remote owns the name, and similar.</param>
public sealed record ProxyConfigSettings(SettingsChangeSet Changes, IReadOnlyList<string> ImportedRoutes, IReadOnlyList<string> Notes);

/// <summary>
/// Works out how an import changes the machine settings file, from the file's current
/// content. The remote is added or updated under its name, with an endpoint of
/// <c>https://&lt;hostname&gt;:&lt;port&gt;</c> and its own tenant and client id only where they
/// differ from <c>AzureAd</c>; blank AzureAd ids are filled from the file instead. A file that
/// carries a scope other than the default sets the remote's <c>Scopes</c> to
/// <c>api://{clientId}/{scope}</c>. Every other key of an existing remote (host rules and the
/// like) is kept. The server's
/// routes are merged in with <c>Remote</c> set to that remote, replacing the routes an earlier
/// import from the same server added and any route already naming that remote; a route name
/// another remote uses is left alone and reported. Pure.
/// </summary>
public static class ProxyConfigSettingsMerge
{
    private const string RemotesPath = "DevConduit:Remotes";
    private const string RoutesPath = "DevConduit:Routes";

    /// <exception cref="InvalidOperationException">The remote name is taken by a remote for another server.</exception>
    public static ProxyConfigSettings Compute(JsonObject machineSettings, ValidatedProxyConfig config, IReadOnlyCollection<string> previouslyImportedRoutes)
    {
        ArgumentNullException.ThrowIfNull(machineSettings);
        ArgumentNullException.ThrowIfNull(config);

        var notes = new List<string>();
        var changes = new SettingsChangeSet();

        var azureAd = Section(machineSettings, "AzureAd");
        var azureTenant = Text(azureAd?["TenantId"]);
        var azureClient = Text(azureAd?["ClientId"]);
        var azureBlank = IsBlank(azureTenant) || IsBlank(azureClient);

        if (azureBlank)
        {
            changes.Set(ManagedSettingKeys.TenantId, config.TenantId);
            changes.Set(ManagedSettingKeys.ClientId, config.ClientId);
            azureTenant = config.TenantId;
            azureClient = config.ClientId;
            notes.Add($"AzureAd was blank, so it is now tenant {config.TenantId}, client {config.ClientId}.");
        }

        var remotes = (Section(Section(machineSettings, "DevConduit"), "Remotes")?.DeepClone() as JsonObject) ?? new JsonObject(SettingsJson.NodeOptions);
        var existingName = Find(remotes, config.Remote);
        if (existingName is not null && remotes[existingName] is { } existing && !PointsAt(existing, config.Hostname))
        {
            throw new InvalidOperationException(
                $"DevConduit:Remotes already has a remote named '{existingName}' for another server. Rename or remove it, then import again.");
        }

        // A re-import changes only what it manages: the endpoint, the remote's own tenant and
        // client id, present only where they differ from AzureAd, and the scope the file names.
        // Host rules and anything else a person added to the remote stay as they were.
        var remote = existingName is not null && remotes[existingName] is JsonObject previousRemote
            ? (JsonObject)previousRemote.DeepClone()
            : new JsonObject(SettingsJson.NodeOptions);
        SetOrRemove(remote, "Endpoint", config.Endpoint);
        SetOrRemove(remote, "TenantId", string.Equals(config.TenantId, azureTenant, StringComparison.OrdinalIgnoreCase) ? null : config.TenantId);
        SetOrRemove(remote, "ClientId", string.Equals(config.ClientId, azureClient, StringComparison.OrdinalIgnoreCase) ? null : config.ClientId);

        // The server's scope travels in the file. A file without one leaves the remote's scopes
        // as they are; the default scope needs no entry, so the default then applies.
        if (config.Scope is { } scope && SetScopes(remote, config.ClientId, scope) is { } requested)
        {
            notes.Add($"The remote requests the scope {requested}.");
        }

        if (existingName is not null)
        {
            remotes.Remove(existingName);
        }

        remotes[config.Remote] = remote;
        changes.Set(RemotesPath, remotes);

        var routes = (Section(Section(machineSettings, "DevConduit"), "Routes")?.DeepClone() as JsonObject) ?? new JsonObject(SettingsJson.NodeOptions);
        var replaced = new HashSet<string>(previouslyImportedRoutes, StringComparer.OrdinalIgnoreCase);
        foreach (var name in routes.Select(route => route.Key).ToList())
        {
            var owner = RemoteOf(routes[name]);
            var ownedHere = string.Equals(owner, config.Remote, StringComparison.OrdinalIgnoreCase);
            if (ownedHere || (replaced.Contains(name) && string.IsNullOrEmpty(owner)))
            {
                routes.Remove(name);
            }
        }

        var imported = new List<string>();
        foreach (var (name, target) in config.Routes)
        {
            if (Find(routes, name) is { } taken)
            {
                notes.Add($"Route '{name}' was not imported: the name is already used by {Describe(routes[taken])}.");
                continue;
            }

            routes[name] = new JsonObject(SettingsJson.NodeOptions) { ["Target"] = target, ["Remote"] = config.Remote };
            imported.Add(name);
        }

        changes.Set(RoutesPath, routes);
        return new ProxyConfigSettings(changes, imported, notes);
    }

    /// <summary>Sets <paramref name="name"/> under whatever casing the entry already uses, or removes it when <paramref name="value"/> is null.</summary>
    private static void SetOrRemove(JsonObject entry, string name, string? value)
    {
        var actual = Find(entry, name);
        if (value is null)
        {
            if (actual is not null)
            {
                entry.Remove(actual);
            }

            return;
        }

        entry[actual ?? name] = value;
    }

    /// <summary>The scope URI a client requests for <paramref name="scope"/> on the app registration <paramref name="clientId"/>.</summary>
    public static string ScopeUri(string clientId, string scope) => $"api://{clientId.Trim()}/{scope.Trim()}";

    /// <summary>
    /// Writes <c>Scopes</c> for a scope other than the default and returns the URI written. For
    /// the default scope, removes only a <c>Scopes</c> an import would have written (one
    /// <c>api://{clientId}/{name}</c> entry), so a list a person wrote stays, and returns null.
    /// </summary>
    private static string? SetScopes(JsonObject remote, string clientId, string scope)
    {
        var actual = Find(remote, "Scopes");
        if (!ApiScopeOptions.IsDefault(scope))
        {
            var uri = ScopeUri(clientId, scope);
            remote[actual ?? "Scopes"] = new JsonArray(uri);
            return uri;
        }

        if (actual is not null && IsImportedScopes(remote[actual], clientId))
        {
            remote.Remove(actual);
        }

        return null;
    }

    private static bool IsImportedScopes(JsonNode? node, string clientId)
    {
        var prefix = ScopeUri(clientId, string.Empty);
        return node is JsonArray { Count: 1 } array
            && Text(array[0]) is { } value
            && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && ProxyConfigValidator.IsScopeName(value[prefix.Length..]);
    }

    internal static bool PointsAt(JsonNode remote, string hostname)
    {
        var endpoint = remote is JsonObject entry ? Text(entry[Find(entry, "Endpoint") ?? "Endpoint"]) : Text(remote);
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, hostname, StringComparison.OrdinalIgnoreCase);
    }

    internal static string? RemoteOf(JsonNode? route) =>
        route is JsonObject entry && Find(entry, "Remote") is { } name ? Text(entry[name]) : null;

    private static string Describe(JsonNode? route) =>
        RemoteOf(route) is { Length: > 0 } remote ? $"a route for the remote '{remote}'" : "a route for the default remote";

    internal static JsonObject? Section(JsonObject? parent, string name) =>
        parent is not null && Find(parent, name) is { } actual ? parent[actual] as JsonObject : null;

    internal static string? Find(JsonObject parent, string name) =>
        parent.Select(property => property.Key).FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));

    internal static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : null;

    private static bool IsBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.TrimStart().StartsWith('<') || value.Contains("YOUR-", StringComparison.OrdinalIgnoreCase);
}
