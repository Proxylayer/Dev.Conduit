using Proxylayer.DevConduit.Models;

namespace Proxylayer.DevConduit.Services.Local;

/// <summary>
/// Admission control for <c>/route</c> in local mode. There is deliberately no sign-in to
/// check: requiring one on the local hop would put a token back into the application that
/// local mode exists to keep it out of. The listener is loopback-only, and an optional
/// shared secret narrows that further.
/// </summary>
public sealed class LocalRouteAuthorizer : IRouteAuthorizer
{
    private readonly LocalSharedSecret _secret;

    public LocalRouteAuthorizer(LocalSharedSecret secret)
    {
        _secret = secret;
    }

    public async Task<bool> TryAuthorizeAsync(HttpContext context)
    {
        if (_secret.Accepts(context.Request.Headers[DevConduitHeaderNames.LocalSecret].ToString()))
        {
            return true;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response
            .WriteAsJsonAsync(new RouteFailure("local_secret_required", _secret.Requirement))
            .ConfigureAwait(false);

        return false;
    }
}
