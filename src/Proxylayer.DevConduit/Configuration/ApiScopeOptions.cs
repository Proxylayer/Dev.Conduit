namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// The delegated scopes a bearer token must carry to use the forwarding endpoint,
/// bound from "AzureAd:Scopes".
/// </summary>
public sealed class ApiScopeOptions
{
    public const string SectionName = "AzureAd";
    public const string DefaultScope = "DevConduit.Forward";

    /// <summary>Accepted scope names. Empty means <see cref="DefaultScope"/>.</summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>The configured scopes, or the built-in default when none are configured.</summary>
    public IReadOnlyList<string> Accepted
    {
        get
        {
            var configured = Scopes
                .Where(scope => !string.IsNullOrWhiteSpace(scope))
                .Select(scope => scope.Trim())
                .ToArray();

            return configured.Length == 0 ? [DefaultScope] : configured;
        }
    }

    /// <summary>The first accepted scope: the one an export tells clients to request.</summary>
    public string Primary => Accepted[0];

    /// <summary>True for the built-in scope name, ignoring case as the bearer scope check does.</summary>
    public static bool IsDefault(string? scope) =>
        string.Equals(scope?.Trim(), DefaultScope, StringComparison.OrdinalIgnoreCase);
}
