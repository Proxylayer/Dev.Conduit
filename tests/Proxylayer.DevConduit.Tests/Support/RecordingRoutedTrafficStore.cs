using Proxylayer.DevConduit.Models;
using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Tests.Support;

/// <summary>An in-memory routed-traffic store, so capture never touches the filesystem.</summary>
internal sealed class RecordingRoutedTrafficStore : IRoutedTrafficStore
{
    public List<RoutedEntry> Entries { get; } = [];

    public Task AppendAsync(string userId, RoutedEntry entry, CancellationToken cancellationToken)
    {
        lock (Entries)
        {
            Entries.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(string userId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> DeleteAsync(string userId, string entryId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<RoutedEntry?> GetAsync(string userId, string entryId, CancellationToken cancellationToken) =>
        Task.FromResult<RoutedEntry?>(null);

    public Task<HistoryPage> GetPageAsync(string userId, int skip, int take, string? query, CancellationToken cancellationToken) =>
        Task.FromResult(new HistoryPage(0, 0, 0, []));
}
