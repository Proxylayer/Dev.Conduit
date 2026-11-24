using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Endpoints;

/// <summary>Read and delete operations over the signed-in user's routed traffic.</summary>
public static class RoutedHistoryEndpoints
{
    public static RouteGroupBuilder MapRoutedHistoryEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/routed", async (
            IRoutedTrafficStore store,
            IUserContext user,
            CancellationToken cancellationToken,
            int skip = 0,
            int take = 50,
            string? q = null) =>
        {
            var page = await store.GetPageAsync(user.UserId, skip, take, q, cancellationToken);
            return Results.Ok(page);
        });

        api.MapGet("/routed/{id}", async (
            string id,
            IRoutedTrafficStore store,
            IUserContext user,
            CancellationToken cancellationToken) =>
        {
            var entry = await store.GetAsync(user.UserId, id, cancellationToken);
            return entry is null ? Results.NotFound() : Results.Ok(entry);
        });

        api.MapDelete("/routed/{id}", async (
            string id,
            IRoutedTrafficStore store,
            IUserContext user,
            CancellationToken cancellationToken) =>
        {
            var deleted = await store.DeleteAsync(user.UserId, id, cancellationToken);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        api.MapDelete("/routed", async (
            IRoutedTrafficStore store,
            IUserContext user,
            CancellationToken cancellationToken) =>
        {
            await store.ClearAsync(user.UserId, cancellationToken);
            return Results.NoContent();
        });

        return api;
    }
}
