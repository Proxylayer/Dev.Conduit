using Proxylayer.DevConduit.Models;
using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Endpoints;

/// <summary>Wires up the authenticated <c>/api</c> surface.</summary>
public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapDevConduitApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints
            .MapGroup("/api")
            .RequireAuthorization()
            .AddEndpointFilter<ClientHeaderFilter>();

        api.MapGet("/me", (IUserContext user) =>
            Results.Ok(new { user.UserId, user.DisplayName, user.UserName }));

        api.MapProxyEndpoints();
        api.MapHistoryEndpoints();
        api.MapRoutedHistoryEndpoints();
        api.MapHeaderSetEndpoints();
        api.MapStorageEndpoints();

        return endpoints;
    }
}
