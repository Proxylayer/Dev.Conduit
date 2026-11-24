using System.Security.Claims;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Resolves the signed-in developer from the current <see cref="HttpContext"/>.
/// The identifier is validated before it can ever reach a file path.
/// </summary>
public sealed class UserContext : IUserContext
{
    private const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    private const string ShortObjectIdClaim = "oid";
    private const string PreferredUsernameClaim = "preferred_username";
    private const string UpnClaim = "upn";

    public UserContext(IHttpContextAccessor httpContextAccessor)
    {
        var user = httpContextAccessor.HttpContext?.User
                   ?? throw new InvalidOperationException("No HttpContext is available for the current operation.");

        if (user.Identity?.IsAuthenticated != true)
        {
            throw new InvalidOperationException("The current request is not authenticated.");
        }

        var userId = FirstNonEmpty(user, ObjectIdClaim, ShortObjectIdClaim, "sub", ClaimTypes.NameIdentifier)
                     ?? throw new InvalidOperationException("The signed-in principal carries no object identifier claim.");

        if (!UserStoragePaths.IsSafeSegment(userId))
        {
            throw new InvalidOperationException("The signed-in principal's identifier is not usable as a storage key.");
        }

        UserId = userId;
        UserName = FirstNonEmpty(user, PreferredUsernameClaim, UpnClaim, ClaimTypes.Upn, ClaimTypes.Email) ?? userId;
        DisplayName = FirstNonEmpty(user, "name", ClaimTypes.Name) ?? UserName;
    }

    public string UserId { get; }

    public string DisplayName { get; }

    public string UserName { get; }

    private static string? FirstNonEmpty(ClaimsPrincipal user, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = user.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
