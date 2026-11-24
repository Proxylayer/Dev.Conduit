using Proxylayer.DevConduit.Models;

namespace Proxylayer.DevConduit.Services;

/// <summary>Per-user persistence of request history and saved header sets.</summary>
public interface IUserStore
{
    Task<HistoryPage> GetHistoryAsync(string userId, int skip, int take, string? query, CancellationToken cancellationToken);

    Task<HistoryEntry?> GetHistoryEntryAsync(string userId, string entryId, CancellationToken cancellationToken);

    /// <summary>Persists an executed request, applying body caps and pruning the oldest entries.</summary>
    Task<HistoryEntry> AppendHistoryAsync(string userId, HistoryEntry entry, CancellationToken cancellationToken);

    Task<bool> DeleteHistoryEntryAsync(string userId, string entryId, CancellationToken cancellationToken);

    Task ClearHistoryAsync(string userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SavedHeaderSet>> GetHeaderSetsAsync(string userId, CancellationToken cancellationToken);

    Task<SavedHeaderSet> CreateHeaderSetAsync(string userId, SavedHeaderSetInput input, CancellationToken cancellationToken);

    Task<SavedHeaderSet?> ReplaceHeaderSetAsync(string userId, string setId, SavedHeaderSetInput input, CancellationToken cancellationToken);

    Task<bool> DeleteHeaderSetAsync(string userId, string setId, CancellationToken cancellationToken);
}
