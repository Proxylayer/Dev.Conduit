using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Models;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Authenticates and authorizes a <c>/route</c> call. Bearer is tried first because that
/// is how programmatic callers arrive; the browser session is the fallback so a developer
/// can exercise the endpoint by hand. The delegated scope is required of bearer callers
/// only: a cookie session is backed by an id_token and carries no scope claim.
/// </summary>
public sealed class RouteAuthorizer : IRouteAuthorizer
{
    private readonly IAuthorizationService _authorization;
    private readonly BearerScopeGuard _scopes;
    private readonly ILogger<RouteAuthorizer> _logger;

    public RouteAuthorizer(IAuthorizationService authorization, BearerScopeGuard scopes, ILogger<RouteAuthorizer> logger)
    {
        _authorization = authorization;
        _scopes = scopes;
        _logger = logger;
    }

    /// <summary>
    /// Returns true when the call may proceed. Otherwise the response has already been
    /// completed with a challenge, a forbid or a 403 body.
    /// </summary>
    public async Task<bool> TryAuthorizeAsync(HttpContext context)
    {
        var bearer = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme).ConfigureAwait(false);
        var viaBearer = bearer.Succeeded;

        var result = viaBearer
            ? bearer
            : await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);

        if (!result.Succeeded || result.Principal is null)
        {
            await ChallengeAsync(context).ConfigureAwait(false);
            return false;
        }

        context.User = result.Principal;

        var authorized = await _authorization
            .AuthorizeAsync(result.Principal, DevConduitAuthorization.RouteAccessPolicy)
            .ConfigureAwait(false);

        if (!authorized.Succeeded)
        {
            await ForbidAsync(context, "not_allowed", "The signed-in principal is not on the Dev.Conduit allow list.")
                .ConfigureAwait(false);
            return false;
        }

        if (viaBearer && !_scopes.HasAcceptedScope(result.Principal))
        {
            await ForbidAsync(
                    context,
                    "insufficient_scope",
                    $"The access token is missing a required delegated scope ({string.Join(", ", _scopes.Accepted)}).")
                .ConfigureAwait(false);
            return false;
        }

        return true;
    }

    /// <summary>
    /// A browser gets the interactive sign-in redirect; anything else gets a bearer
    /// challenge, because a 302 to a login page is useless to a programmatic caller.
    /// </summary>
    private static Task ChallengeAsync(HttpContext context) =>
        WantsHtml(context.Request)
            ? context.ChallengeAsync(CookieAuthenticationDefaults.AuthenticationScheme)
            : context.ChallengeAsync(JwtBearerDefaults.AuthenticationScheme);

    private async Task ForbidAsync(HttpContext context, string kind, string message)
    {
        _logger.LogInformation("Refusing a /route call: {Kind}.", kind);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new RouteFailure(kind, message)).ConfigureAwait(false);
    }

    private static bool WantsHtml(HttpRequest request) =>
        request.Headers.Accept.Any(value => value is not null && value.Contains("text/html", StringComparison.OrdinalIgnoreCase));
}
