using Proxylayer.DevConduit.Models;

namespace Proxylayer.DevConduit.Services;

/// <summary>Performs the outbound call described by a <see cref="ProxyRequest"/>.</summary>
public interface IProxyExecutor
{
    Task<ProxyExecutionResult> ExecuteAsync(ProxyRequest request, CancellationToken cancellationToken);
}
