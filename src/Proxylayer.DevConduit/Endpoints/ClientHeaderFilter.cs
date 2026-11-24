namespace Proxylayer.DevConduit.Endpoints;

/// <summary>
/// Requires the UI's marker header on every API call. Combined with SameSite=Lax
/// cookies this stops a foreign origin from driving the API with the user's session,
/// because a cross-site form or image request cannot set custom headers.
/// </summary>
public sealed class ClientHeaderFilter : IEndpointFilter
{
    public const string HeaderName = "X-DevConduit-Client";
    public const string HeaderValue = "1";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var header = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (!string.Equals(header, HeaderValue, StringComparison.Ordinal))
        {
            return Results.Problem(
                title: "Missing client header",
                detail: $"The '{HeaderName}: {HeaderValue}' request header is required on every /api call.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return await next(context);
    }
}
