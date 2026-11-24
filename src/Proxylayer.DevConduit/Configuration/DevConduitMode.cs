namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// Which half of the pair this process is running as. One executable serves both roles
/// so the wire contract between them cannot drift.
/// </summary>
public enum DevConduitMode
{
    /// <summary>
    /// Runs on the privileged machine. Validates Entra tokens on the way in and forwards
    /// to the real upstream. The default.
    /// </summary>
    Server,

    /// <summary>
    /// Runs on the developer workstation. Binds loopback only, acquires Entra tokens with
    /// MSAL, and relays to a remote Dev.Conduit instance rather than to the upstream.
    /// </summary>
    Local
}
