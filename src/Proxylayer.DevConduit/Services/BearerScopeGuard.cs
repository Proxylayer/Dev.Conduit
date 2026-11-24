using System.Security.Claims;
using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Checks that a bearer principal carries one of the accepted delegated scopes.
/// Cookie-authenticated developers never reach this check: an id_token-backed session
/// carries no scope claim, and requiring one would lock the browser out of /route.
/// </summary>
public sealed class BearerScopeGuard
{
    // The two claim types Entra issues for delegated scopes, short and SOAP-namespaced.
    private const string ScopeClaim = "scp";
    private const string LongScopeClaim = "http://schemas.microsoft.com/identity/claims/scope";

    private readonly HashSet<string> _accepted;

    public BearerScopeGuard(IOptions<ApiScopeOptions> options)
    {
        _accepted = new HashSet<string>(options.Value.Accepted, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The scopes a token must carry, for diagnostics and error messages.</summary>
    public IReadOnlyCollection<string> Accepted => _accepted;

    public bool HasAcceptedScope(ClaimsPrincipal principal)
    {
        foreach (var claim in principal.FindAll(ScopeClaim).Concat(principal.FindAll(LongScopeClaim)))
        {
            // Entra packs delegated scopes into one space-separated claim value.
            foreach (var scope in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (_accepted.Contains(scope))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
