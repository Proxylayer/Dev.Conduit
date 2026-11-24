namespace Proxylayer.DevConduit.Services;

/// <summary>The signed-in developer, resolved from the current request principal.</summary>
public interface IUserContext
{
    /// <summary>Storage-safe identifier for the signed-in user.</summary>
    string UserId { get; }

    string DisplayName { get; }

    string UserName { get; }
}
