using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Services.Local;

/// <summary>
/// Supplies the access token the handler presents to a remote Dev.Conduit instance. Implemented
/// against MSAL by default; substitute your own when the host already holds a suitable token.
/// </summary>
public interface IDevConduitTokenProvider
{
    /// <summary>
    /// Returns a valid access token for the remote's identity and scopes, acquiring one if
    /// needed.
    /// </summary>
    ValueTask<string> GetAccessTokenAsync(RemoteDefinition remote, CancellationToken cancellationToken);
}
