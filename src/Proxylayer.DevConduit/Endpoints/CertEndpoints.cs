using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Proxylayer.DevConduit.Certificates;

namespace Proxylayer.DevConduit.Endpoints;

/// <summary>
/// <c>GET /cert</c> and <c>POST /cert/renew</c>: how a client that imported a
/// <c>.conduit</c> keeps its pinned server certificates and its own client certificate
/// current. Internal: not part of the documented API and not used by the console. Outside
/// Entra sign-in; instead each request must present a client certificate this server issued,
/// asked for only once the request reaches here, so a browser opening the console is never
/// prompted for one. Anything else is answered with a bare 401 or 403. A renewal is checked
/// again under the store's lock: the certificate must still be accepted (403), be inside its
/// last sixty days (409), and ask for its enrollment's subject (400). Rate limited per IP.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CertEndpoints
{
    public const string Path = "/cert";
    public const string RateLimitPolicy = "cert";

    /// <summary>Requests allowed per client address per minute.</summary>
    public const int PermitsPerMinute = 30;

    private const int MaxRenewBodyBytes = 16 * 1024;

    public static IServiceCollection AddCertEndpointRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PermitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
        });

    public static IEndpointRouteBuilder MapCertEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var cert = endpoints.MapGroup(Path)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .ExcludeFromDescription();

        cert.MapGet(string.Empty, GetAsync);
        cert.MapPost("/renew", RenewAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(HttpContext context, ServerCertificateManager manager, TimeProvider clock)
    {
        var (presented, refusal) = await AuthenticateAsync(context, manager, clock).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        var set = manager.Certificates;
        var notAfter = presented!.NotAfter.ToUniversalTime();
        return Results.Json(new CertResponse(
            set.Hostname,
            set.Published.Select(Publish).ToList(),
            new ClientCertificateStatus(notAfter, presented.SerialNumber, ClientCertificateRenewal.RenewAfter(notAfter))));
    }

    private static async Task<IResult> RenewAsync(HttpContext context, ServerCertificateManager manager, TimeProvider clock, ILoggerFactory loggers)
    {
        // The body is read before the client certificate is asked for: a TLS 1.2 renegotiation
        // cannot start while request data is still in flight.
        var body = await ReadBodyAsync(context).ConfigureAwait(false);

        var (presented, refusal) = await AuthenticateAsync(context, manager, clock).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        byte[] signingRequest;
        try
        {
            var request = body is null ? null : System.Text.Json.JsonSerializer.Deserialize<CertRenewRequest>(body, System.Text.Json.JsonSerializerOptions.Web);
            signingRequest = Convert.FromBase64String(request?.Csr ?? string.Empty);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or FormatException)
        {
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        if (signingRequest.Length == 0)
        {
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        try
        {
            var issued = manager.Authority.Renew(signingRequest, presented!);
            using var certificate = issued.Certificate;
            return Results.Json(new CertRenewResponse(
                Convert.ToBase64String(certificate.RawData),
                issued.Record.Serial,
                issued.Record.NotAfter));
        }
        catch (CertificateRenewalException exception)
        {
            loggers.CreateLogger(typeof(CertEndpoints).FullName!).LogInformation("Refused a client certificate renewal: {Reason}", exception.Message);
            return Results.StatusCode(exception.Refusal switch
            {
                CertificateRenewalRefusal.NotAccepted => StatusCodes.Status403Forbidden,
                CertificateRenewalRefusal.NotDue => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest
            });
        }
    }

    /// <summary>The request body, or null when it is larger than a renewal request can be or cannot be read.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength is > MaxRenewBodyBytes)
        {
            return null;
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
        {
            bodySize.MaxRequestBodySize = MaxRenewBodyBytes;
        }

        try
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted).ConfigureAwait(false);
            return buffer.Length > MaxRenewBodyBytes ? null : buffer.ToArray();
        }
        catch (Exception exception) when (exception is BadHttpRequestException or IOException)
        {
            return null;
        }
    }

    /// <summary>The presented certificate when this server issued it and still accepts it; otherwise the bare refusal.</summary>
    private static async Task<(X509Certificate2? Certificate, IResult? Refusal)> AuthenticateAsync(
        HttpContext context,
        ServerCertificateManager manager,
        TimeProvider clock)
    {
        if (!context.Request.IsHttps)
        {
            return (null, Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        X509Certificate2? presented;
        try
        {
            presented = await context.Connection.GetClientCertificateAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.Security.Authentication.AuthenticationException)
        {
            presented = null;
        }

        if (presented is null)
        {
            return (null, Results.StatusCode(StatusCodes.Status401Unauthorized));
        }

        var set = manager.Certificates;
        var ledger = manager.Authority.ReadIssued();
        return ClientCertificateValidator.IsAccepted(presented, set.ClientAuthority, ledger, clock.GetUtcNow())
            ? (presented, null)
            : (null, Results.StatusCode(StatusCodes.Status403Forbidden));
    }

    private static PublishedServerCertificate Publish(X509Certificate2 certificate) =>
        new(
            Convert.ToBase64String(certificate.RawData),
            certificate.NotBefore.ToUniversalTime(),
            certificate.NotAfter.ToUniversalTime(),
            certificate.Thumbprint);
}
