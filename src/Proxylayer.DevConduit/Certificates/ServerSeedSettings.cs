using System.Text.Json.Nodes;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>
/// What a server takes from another server's <c>.conduit</c> to seed its own machine settings:
/// the Entra tenant and client ids, the scope the file names (a file without one leaves
/// <c>AzureAd:Scopes</c> alone), and the file's routes as its own <c>DevConduit:Routes</c>,
/// each a name and a target with no remote, replacing the routes it had. Nothing else: the
/// host name, port and client address stay this machine's own, and the file's certificates,
/// client certificate, address and remote are ignored, so neither the hosts file, the pins,
/// a client identity nor Windows trust is touched. The managed identity is not in the file.
/// The change touches only <see cref="ManagedSettingKeys.Seed"/>. Pure.
/// </summary>
public sealed record ServerSeedSettings(
    string TenantId,
    string ClientId,
    IReadOnlyList<KeyValuePair<string, string>> Routes,
    string? Scope = null)
{
    /// <summary>Said after every seed: the one Entra setting the file cannot carry.</summary>
    public const string ManagedIdentityNote =
        "The managed identity client id is not in the file: set it on the Server page (AzureAd:ClientCredentials) if this server uses one.";

    public static ServerSeedSettings From(ValidatedProxyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new ServerSeedSettings(config.TenantId, config.ClientId, config.Routes, config.Scope);
    }

    /// <summary>The change to the machine settings file.</summary>
    public SettingsChangeSet Changes
    {
        get
        {
            var changes = new SettingsChangeSet()
                .Set(ManagedSettingKeys.TenantId, TenantId)
                .Set(ManagedSettingKeys.ClientId, ClientId);

            // The default scope needs no entry; a file that names none leaves the scope as it is.
            if (Scope is { } scope)
            {
                changes = ApiScopeOptions.IsDefault(scope)
                    ? changes.Remove(ManagedSettingKeys.Scopes)
                    : changes.Set(ManagedSettingKeys.Scopes, new JsonArray(scope));
            }

            var routes = new JsonObject();
            foreach (var (name, target) in Routes)
            {
                routes[name] = target;
            }

            return routes.Count == 0 ? changes.Remove(ManagedSettingKeys.Routes) : changes.Set(ManagedSettingKeys.Routes, routes);
        }
    }

    /// <summary>What the seed changes in <paramref name="machineSettings"/>, one line each, for the confirmation and the command line.</summary>
    public IReadOnlyList<string> Describe(JsonObject machineSettings)
    {
        ArgumentNullException.ThrowIfNull(machineSettings);

        var azureAd = Section(machineSettings, "AzureAd");
        var existingRoutes = Section(Section(machineSettings, "DevConduit"), "Routes")?.Count ?? 0;
        var scope = Scope is null
            ? "Scope: not in the file, left as it is"
            : $"Scope: {Scope}{Was(FirstScope(azureAd) ?? ApiScopeOptions.DefaultScope, Scope)}";

        return
        [
            $"Tenant id: {TenantId}{Was(Text(azureAd, "TenantId"), TenantId)}",
            $"Client id: {ClientId}{Was(Text(azureAd, "ClientId"), ClientId)}",
            scope,
            $"Routes: {Routes.Count} ({(Routes.Count == 0 ? "none" : string.Join(", ", Routes.Select(route => route.Key)))})"
                + (existingRoutes == 0 ? string.Empty : $", replacing the {existingRoutes} route{(existingRoutes == 1 ? string.Empty : "s")} set now")
        ];
    }

    private static string Was(string? current, string value) =>
        string.IsNullOrWhiteSpace(current) ? " (not set now)"
        : string.Equals(current.Trim(), value, StringComparison.OrdinalIgnoreCase) ? " (unchanged)"
        : $" (now {current.Trim()})";

    private static string? FirstScope(JsonObject? azureAd) =>
        azureAd?.FirstOrDefault(property => string.Equals(property.Key, "Scopes", StringComparison.OrdinalIgnoreCase)).Value switch
        {
            JsonArray array => array.Select(entry => entry is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
                .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)),
            JsonValue value when value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) => text,
            _ => null
        };

    private static JsonObject? Section(JsonObject? parent, string name) =>
        parent?.FirstOrDefault(property => string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase)).Value as JsonObject;

    private static string? Text(JsonObject? parent, string name) =>
        parent?.FirstOrDefault(property => string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase)).Value is JsonValue value
        && value.TryGetValue<string>(out var text)
            ? text
            : null;
}
