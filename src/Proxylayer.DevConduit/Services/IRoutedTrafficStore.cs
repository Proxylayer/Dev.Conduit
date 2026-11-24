using Proxylayer.DevConduit.Models;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Per-user persistence of traffic forwarded through <c>/route</c>. Deliberately separate
/// from <see cref="IUserStore"/> so routed volume can never prune console history.
/// </summary>
public interface IRoutedTrafficStore
{
    Task<HistoryPage> GetPageAsync(string userId, int skip, int take, string? query, CancellationToken cancellationToken);

    Task<RoutedEntry?> GetAsync(string userId, string entryId, CancellationToken cancellationToken);

    Task AppendAsync(string userId, RoutedEntry entry, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string userId, string entryId, CancellationToken cancellationToken);

    Task ClearAsync(string userId, CancellationToken cancellationToken);
}
