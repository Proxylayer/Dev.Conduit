using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Endpoints;

/// <summary>Read and delete operations over the signed-in user's request history.</summary>
public static class HistoryEndpoints
{
    public static RouteGroupBuilder MapHistoryEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/history", async (
            IUserStore store,
            IUserContext user,
            CancellationToken cancellationToken,
            int skip = 0,
            int take = 50,
            string? q = null) =>
        {
            var page = await store.GetHistoryAsync(user.UserId, skip, take, q, cancellationToken);
            return Results.Ok(page);
        });

        api.MapGet("/history/{id}", async (
            string id,
            IUserStore store,
            IUserContext user,
            CancellationToken cancellationToken) =>
        {
            var entry = await store.GetHistoryEntryAsync(user.UserId, id, cancellationToken);
            return entry is null ? Results.NotFound() : Results.Ok(entry);
        });

        api.MapDelete("/history/{id}", async (
            string id,
            IUserStore store,
            IUserContext user,
            CancellationToken cancellationToken) =>
        {
            var deleted = await store.DeleteHistoryEntryAsync(user.UserId, id, cancellationToken);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        api.MapDelete("/history", async (
            IUserStore store,
            IUserContext user,
            CancellationToken cancellationToken) =>
        {
            await store.ClearHistoryAsync(user.UserId, cancellationToken);
            return Results.NoContent();
        });

        return api;
    }
}
