using Microsoft.AspNetCore.Authorization;

namespace Proxylayer.DevConduit.Configuration;

/// <summary>Builds the authorization policies the tool runs under.</summary>
public static class DevConduitAuthorization
{
    /// <summary>
    /// Policy applied to the /route forwarding endpoint. Programmatic callers present a
    /// bearer token; a signed-in developer can exercise the same endpoint from the browser.
    /// </summary>
    public const string RouteAccessPolicy = "RouteAccess";

    private const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    private const string GroupsClaim = "groups";

    public static AuthorizationPolicy BuildPolicy(DevConduitOptions options) =>
        Configure(new AuthorizationPolicyBuilder(), options).Build();

    /// <summary>
    /// The same allow-list rules as <see cref="BuildPolicy"/>, but accepting either a bearer
    /// token or the browser session cookie.
    /// </summary>
    public static AuthorizationPolicy BuildRoutePolicy(DevConduitOptions options, params string[] schemes) =>
        Configure(new AuthorizationPolicyBuilder(schemes), options).Build();

    private static AuthorizationPolicyBuilder Configure(AuthorizationPolicyBuilder builder, DevConduitOptions options)
    {
        builder.RequireAuthenticatedUser();

        var allowedUsers = options.AllowedUserOids
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allowedGroups = options.AllowedGroupIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (allowedUsers.Count == 0 && allowedGroups.Count == 0)
        {
            return builder;
        }

        builder.RequireAssertion(context =>
        {
            var oid = context.User.FindFirst(ObjectIdClaim)?.Value ?? context.User.FindFirst("oid")?.Value;
            if (oid is not null && allowedUsers.Contains(oid))
            {
                return true;
            }

            return context.User.FindAll(GroupsClaim).Any(claim => allowedGroups.Contains(claim.Value));
        });

        return builder;
    }
}
