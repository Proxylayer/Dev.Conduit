namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// The Entra identity local mode signs in with to call one remote Dev.Conduit instance: the
/// tenant and the application (client) id of the app registration that remote validates
/// tokens against.
/// </summary>
public sealed record RemoteIdentity(string TenantId, string ClientId)
{
    /// <summary>True once a tenant and client id are present, so a sign-in could succeed.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !TenantId.TrimStart().StartsWith('<')
        && !ClientId.TrimStart().StartsWith('<');

    /// <summary>
    /// Identifies the identity regardless of how the ids are cased, so two remotes that share
    /// an app registration share one sign-in.
    /// </summary>
    public string Key => $"{TenantId}|{ClientId}".ToUpperInvariant();
}

/// <summary>
/// One entry of <c>DevConduit:Remotes</c> with every default applied: the identity inherited
/// from <c>AzureAd</c> where the entry names none, and the default Dev.Conduit API scope where
/// it names no scopes. Built by <see cref="RoutingConfigurationParser"/>.
/// </summary>
/// <param name="Name">The remote's name, the key under <c>DevConduit:Remotes</c>.</param>
/// <param name="Endpoint">The endpoint exactly as configured, trimmed.</param>
/// <param name="BaseAddress">The endpoint's origin with a trailing slash, or null when it is unusable.</param>
/// <param name="Identity">The effective tenant and client id.</param>
/// <param name="InheritsTenantId">True when the tenant came from <c>AzureAd:TenantId</c>.</param>
/// <param name="InheritsClientId">True when the client id came from <c>AzureAd:ClientId</c>.</param>
/// <param name="Scopes">The effective scopes requested for this remote.</param>
/// <param name="Targets">Extra forward-proxy host rules relayed to this remote, as configured.</param>
public sealed record RemoteDefinition(
    string Name,
    string Endpoint,
    Uri? BaseAddress,
    RemoteIdentity Identity,
    bool InheritsTenantId,
    bool InheritsClientId,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Targets)
{
    /// <summary>The scope name the Dev.Conduit app registration exposes.</summary>
    public const string DefaultScopeName = "DevConduit.Forward";

    /// <summary>The configuration path of this remote, for messages.</summary>
    public string ConfigurationPath => $"DevConduit:Remotes:{Name}";

    /// <summary>Where the identity comes from, for the startup banner.</summary>
    public string DescribeIdentity() =>
        (InheritsTenantId, InheritsClientId) switch
        {
            (true, true) => "inherits AzureAd",
            (true, false) => $"client {Identity.ClientId} (tenant from AzureAd)",
            (false, true) => $"tenant {Identity.TenantId} (client from AzureAd)",
            _ => $"client {Identity.ClientId}, tenant {Identity.TenantId}"
        };
}
