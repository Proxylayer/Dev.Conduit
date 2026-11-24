namespace Proxylayer.DevConduit.Configuration;

/// <summary>Which kind of client credential the app authenticates to Entra ID with.</summary>
public enum EntraCredentialKind
{
    None = 0,
    ClientSecret,
    ManagedIdentity,
    Other
}

/// <summary>
/// The client credential configured under <c>AzureAd</c>, described for diagnostics.
/// <see cref="Detail"/> is safe to log: it never carries a secret value.
/// </summary>
/// <param name="Kind">The credential kind in use.</param>
/// <param name="Detail">A readable description of where the credential comes from.</param>
/// <param name="ManagedIdentityClientId">
/// The client id of the user-assigned managed identity; null or empty for a system-assigned one.
/// Only meaningful when <paramref name="Kind"/> is <see cref="EntraCredentialKind.ManagedIdentity"/>.
/// </param>
/// <param name="SourceType">The <c>SourceType</c> of the <c>ClientCredentials</c> entry in use, if any.</param>
public sealed record EntraCredential(
    EntraCredentialKind Kind,
    string Detail,
    string? ManagedIdentityClientId = null,
    string? SourceType = null)
{
    public const string ManagedIdentitySourceType = "SignedAssertionFromManagedIdentity";
    public const string ClientSecretSourceType = "ClientSecret";

    /// <summary>True for a managed identity credential with no client id, which means system-assigned.</summary>
    public bool IsSystemAssignedManagedIdentity =>
        Kind == EntraCredentialKind.ManagedIdentity && string.IsNullOrEmpty(ManagedIdentityClientId);

    /// <summary>
    /// Describes the credential in an <c>AzureAd</c> configuration section. The first usable
    /// <c>ClientCredentials</c> entry wins, as Microsoft.Identity.Web tries them in order;
    /// a non-empty top-level <c>ClientSecret</c> is the fallback.
    /// </summary>
    public static EntraCredential Describe(IConfigurationSection azureAd)
    {
        ArgumentNullException.ThrowIfNull(azureAd);

        foreach (var entry in azureAd.GetSection("ClientCredentials").GetChildren())
        {
            if (DescribeEntry(entry) is { } described)
            {
                return described;
            }
        }

        if (!string.IsNullOrEmpty(azureAd["ClientSecret"]))
        {
            return new EntraCredential(EntraCredentialKind.ClientSecret, "client secret (AzureAd:ClientSecret)");
        }

        return new EntraCredential(EntraCredentialKind.None, "no client credential is configured");
    }

    private static EntraCredential? DescribeEntry(IConfigurationSection entry)
    {
        var sourceType = entry["SourceType"]?.Trim();
        if (string.IsNullOrEmpty(sourceType))
        {
            return null;
        }

        var location = $"AzureAd:ClientCredentials:{entry.Key}";

        if (string.Equals(sourceType, ManagedIdentitySourceType, StringComparison.OrdinalIgnoreCase))
        {
            var clientId = entry["ManagedIdentityClientId"]?.Trim();
            var identity = string.IsNullOrEmpty(clientId)
                ? "the system-assigned managed identity"
                : $"user-assigned managed identity {clientId}";
            return new EntraCredential(
                EntraCredentialKind.ManagedIdentity,
                $"federated credential from {identity} ({location})",
                string.IsNullOrEmpty(clientId) ? null : clientId,
                sourceType);
        }

        if (string.Equals(sourceType, ClientSecretSourceType, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrEmpty(entry["ClientSecret"])
                ? null
                : new EntraCredential(EntraCredentialKind.ClientSecret, $"client secret ({location})", SourceType: sourceType);
        }

        return new EntraCredential(
            EntraCredentialKind.Other,
            $"SourceType {sourceType} ({location})",
            SourceType: sourceType);
    }
}
