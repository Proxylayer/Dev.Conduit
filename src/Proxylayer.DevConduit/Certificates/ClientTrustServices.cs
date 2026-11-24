using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>Registers what validating a remote against its pinned certificates needs, once.</summary>
public static class ClientTrustServices
{
    public static IServiceCollection AddClientTrust(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(_ => new ClientTrustStore(ClientTrustLayers.ForThisUser()));
        services.TryAddSingleton<RemoteCertificatePolicy>();
        return services;
    }
}
