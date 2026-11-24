using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Endpoints;

/// <summary>Reports what the signed-in user's stores occupy on disk.</summary>
public static class StorageEndpoints
{
    public static RouteGroupBuilder MapStorageEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/storage", async (
            StorageUsageReporter reporter,
            IUserContext user,
            CancellationToken cancellationToken) =>
        {
            var usage = await reporter.MeasureAsync(user.UserId, cancellationToken);
            return Results.Ok(usage);
        });

        return api;
    }
}
