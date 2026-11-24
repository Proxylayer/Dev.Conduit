namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// Settings that no longer have any effect. A deployment that still carries one would
/// otherwise get a listen address, a remote or a route it did not ask for without a word of
/// explanation, so each one found in configuration is named in a warning that says what
/// replaced it. None of them stops the host, and none is migrated automatically.
/// </summary>
public static class RemovedSettings
{
    private const string ListenAddressReplacement =
        "The server's listen address comes from Kestrel:Endpoints (or urls / ASPNETCORE_URLS) and defaults to "
        + ServerModeStartup.DefaultListenUrl + ".";

    private static readonly (string Key, string Replacement)[] Keys =
    [
        ("DevConduit:Port", ListenAddressReplacement),
        ("DevConduit:BindAllInterfaces", ListenAddressReplacement),
        ("DevConduit:Route:Aliases", "Move each alias to DevConduit:Routes, which takes the same name-to-origin entries."),
        ("DevConduit:Local:RemoteEndpoint", "Name the remote Dev.Conduit instance under DevConduit:Remotes, for example \"Remotes\": { \"uat\": \"https://devconduit.corp:8091\" }."),
        ("DevConduit:Local:Targets", "Move the host rules to DevConduit:Remotes:<name>:Targets on the remote that should carry them; routes under DevConduit:Routes are added to the forward-proxy rules automatically."),
        ("DevConduit:Local:TenantId", "Set TenantId on the remote under DevConduit:Remotes:<name>, or leave it out to inherit AzureAd:TenantId."),
        ("DevConduit:Local:ClientId", "Set ClientId on the remote under DevConduit:Remotes:<name>, or leave it out to inherit AzureAd:ClientId."),
        ("DevConduit:Local:Scopes", "Set Scopes on the remote under DevConduit:Remotes:<name>, or leave it out for api://<client-id>/DevConduit.Forward.")
    ];

    /// <summary>The removed keys present in configuration, in a fixed order.</summary>
    public static IReadOnlyList<string> Find(IConfiguration configuration) =>
        Keys.Where(entry => configuration.GetSection(entry.Key).Exists()).Select(entry => entry.Key).ToList();

    /// <summary>One warning per removed key present in configuration, naming its replacement.</summary>
    public static IReadOnlyList<string> Warnings(IConfiguration configuration) =>
        Keys.Where(entry => configuration.GetSection(entry.Key).Exists())
            .Select(entry => $"{entry.Key} has been removed and is ignored. {entry.Replacement} Remove the key from configuration.")
            .ToList();

    public static void Report(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Dev.Conduit.Startup");

        foreach (var warning in Warnings(app.Configuration))
        {
            logger.LogWarning("{Warning}", warning);
        }
    }
}
