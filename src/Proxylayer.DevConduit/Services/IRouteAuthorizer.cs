namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Decides whether a <c>/route</c> call may proceed. Server mode checks an Entra token or
/// the browser session; local mode has no sign-in to check and relies on the loopback bind,
/// optionally backed by a shared secret.
/// </summary>
public interface IRouteAuthorizer
{
    /// <summary>
    /// Returns true when the call may proceed. Otherwise the response has already been
    /// completed with a challenge, a forbid or an error body.
    /// </summary>
    Task<bool> TryAuthorizeAsync(HttpContext context);
}
