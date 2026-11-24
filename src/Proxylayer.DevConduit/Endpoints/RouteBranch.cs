using Proxylayer.DevConduit.Models;
using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Endpoints;

/// <summary>
/// Mounts the transparent forwarding endpoint as a terminal middleware branch.
/// A branch rather than a mapped endpoint because a proxy has to accept any verb,
/// including the custom ones legacy services define, and route patterns cannot say that.
/// </summary>
public static class RouteBranch
{
    public const string Prefix = "/route";

    public static IApplicationBuilder MapDevConduitRoute(this IApplicationBuilder app)
    {
        app.Map(Prefix, branch => branch.Run(HandleAsync));
        return app;
    }

    private static async Task HandleAsync(HttpContext context)
    {
        var services = context.RequestServices;

        var authorizer = services.GetRequiredService<IRouteAuthorizer>();
        if (!await authorizer.TryAuthorizeAsync(context).ConfigureAwait(false))
        {
            return;
        }

        var resolver = services.GetRequiredService<RouteTargetResolver>();
        if (!resolver.TryResolve(context.Request, out var target, out var failure))
        {
            await RefuseAsync(context, failure).ConfigureAwait(false);
            return;
        }

        // Local mode has to find a remote to carry the target; with several remotes and no
        // default, a target nothing claims has nowhere to go. A server refuses a target its
        // allow-list does not cover.
        if (services.GetRequiredService<IRouteEgress>().Refuse(target) is { } refusal)
        {
            await RefuseAsync(context, refusal).ConfigureAwait(false);
            return;
        }

        // Resolved after authorization so the storage key comes from a verified principal.
        var user = services.GetRequiredService<IUserContext>();
        var forwarder = services.GetRequiredService<RouteForwarder>();

        try
        {
            await forwarder.ForwardAsync(context, target, user.UserId).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A proxy answers 502 for anything it could not complete upstream. An
            // unhandled 500 from here would be indistinguishable from the upstream's own.
            services.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(RouteBranch))
                .LogError(exception, "Forwarding {Method} {Path} failed unexpectedly.", context.Request.Method, context.Request.Path);

            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response
                .WriteAsJsonAsync(new RouteFailure(
                    ProxyErrorKind.Unexpected.ToString(),
                    HttpFailureClassifier.Describe(exception),
                    target.Origin.GetLeftPart(UriPartial.Authority)))
                .ConfigureAwait(false);
        }
    }

    private static async Task RefuseAsync(HttpContext context, RouteResolutionFailure failure)
    {
        context.Response.StatusCode = failure.Status;
        await context.Response
            .WriteAsJsonAsync(new RouteFailure(failure.Kind, failure.Message))
            .ConfigureAwait(false);
    }
}
