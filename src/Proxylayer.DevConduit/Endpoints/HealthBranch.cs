namespace Proxylayer.DevConduit.Endpoints;

/// <summary>
/// Mounts <c>/health</c> as a terminal branch ahead of everything else in the pipeline, sign-in
/// included. A server with no Entra ids cannot even build its OpenID Connect options, so a
/// health probe that reached the authentication middleware would fail with it. Anonymous, in
/// both modes; it answers 200 whenever the process can serve, and says whether the Entra ids
/// are set. Nothing secret is in the answer.
/// </summary>
public static class HealthBranch
{
    public const string Path = "/health";

    /// <param name="configured">False while the Entra tenant or client id is blank or a placeholder.</param>
    public static IApplicationBuilder MapDevConduitHealth(this IApplicationBuilder app, bool configured)
    {
        var body = new HealthStatus("ok", configured);
        app.Map(Path, branch => branch.Run(context => context.Response.WriteAsJsonAsync(body)));
        return app;
    }

    /// <summary>The body of every answer.</summary>
    public sealed record HealthStatus(string Status, bool Configured);
}
