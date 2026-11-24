using System.Text;
using Proxylayer.DevConduit.Models;
using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Endpoints;

/// <summary>The endpoint that runs the composed request and records it in history.</summary>
public static class ProxyEndpoints
{
    public static RouteGroupBuilder MapProxyEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/proxy/execute", ExecuteAsync);
        return api;
    }

    private static async Task<IResult> ExecuteAsync(
        ProxyRequest? request,
        IProxyExecutor executor,
        IUserStore store,
        IUserContext user,
        SecretHeaderPolicy secrets,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "A request body is required." });
        }

        var normalized = request with { Headers = request.Headers ?? [] };
        var result = await executor.ExecuteAsync(normalized, cancellationToken).ConfigureAwait(false);

        var entry = new HistoryEntry(
            HistoryEntry.NewId(),
            DateTimeOffset.UtcNow,
            secrets.Redact(normalized),
            result.Response,
            result.Error,
            result.ErrorKind == ProxyErrorKind.None ? null : result.ErrorKind.ToString(),
            normalized.Body is null ? 0 : Encoding.UTF8.GetByteCount(normalized.Body),
            RequestBodyTruncated: false);

        var stored = await store.AppendHistoryAsync(user.UserId, entry, cancellationToken).ConfigureAwait(false);

        return Results.Ok(new ExecuteProxyResult(
            result.Response,
            result.Error,
            entry.ErrorKind,
            result.DurationMs,
            stored.Id));
    }
}
