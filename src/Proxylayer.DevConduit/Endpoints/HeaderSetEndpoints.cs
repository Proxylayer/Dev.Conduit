using Proxylayer.DevConduit.Models;
using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Endpoints;

/// <summary>CRUD over the signed-in user's reusable header sets.</summary>
public static class HeaderSetEndpoints
{
    public static RouteGroupBuilder MapHeaderSetEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/headers", async (
            IUserStore store,
            IUserContext user,
            CancellationToken cancellationToken) =>
        {
            var sets = await store.GetHeaderSetsAsync(user.UserId, cancellationToken);
            return Results.Ok(sets);
        });

        api.MapPost("/headers", async (
            SavedHeaderSetInput? input,
            IUserStore store,
            IUserContext user,
            CancellationToken cancellationToken) =>
        {
            if (!TryValidate(input, out var error))
            {
                return Results.BadRequest(new { error });
            }

            var created = await store.CreateHeaderSetAsync(user.UserId, input!, cancellationToken);
            return Results.Created($"/api/headers/{created.Id}", created);
        });

        api.MapPut("/headers/{setId}", async (
            string setId,
            SavedHeaderSetInput? input,
            IUserStore store,
            IUserContext user,
            CancellationToken cancellationToken) =>
        {
            if (!TryValidate(input, out var error))
            {
                return Results.BadRequest(new { error });
            }

            var updated = await store.ReplaceHeaderSetAsync(user.UserId, setId, input!, cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        api.MapDelete("/headers/{setId}", async (
            string setId,
            IUserStore store,
            IUserContext user,
            CancellationToken cancellationToken) =>
        {
            var deleted = await store.DeleteHeaderSetAsync(user.UserId, setId, cancellationToken);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        return api;
    }

    private static bool TryValidate(SavedHeaderSetInput? input, out string error)
    {
        if (input is null)
        {
            error = "A header set is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            error = "The header set needs a name.";
            return false;
        }

        if (input.Name.Length > 120)
        {
            error = "The header set name is too long (120 characters maximum).";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
