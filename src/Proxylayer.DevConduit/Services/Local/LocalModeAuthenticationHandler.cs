using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Services.Local;

/// <summary>
/// Authenticates every request in local mode as one fixed pseudo-user. There is no Entra
/// sign-in on the local hop — that absence is what lets an application talk to Dev.Conduit with
/// no token, no headers and no code change — so the console, the API and the storage layer
/// are given a stable principal to work against instead of being made anonymous. The
/// boundary that keeps this safe is the loopback-only bind, not this handler.
/// </summary>
public sealed class LocalModeAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DevConduitLocal";

    private const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    public LocalModeAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ObjectIdClaim, LocalModeOptions.PseudoUserId),
                new Claim("preferred_username", LocalModeOptions.PseudoUserId),
                new Claim("name", "Local mode")
            ],
            SchemeName);

        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
