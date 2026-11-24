using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Models;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Routed traffic on disk under <c>users/{oid}/routed</c>, using the same atomic-write,
/// locking and pruning machinery as console history but with its own caps and gate.
/// </summary>
public sealed class RoutedTrafficStore : IRoutedTrafficStore
{
    private const string LockScope = "routed";

    private static readonly JsonEntryLogShape<RoutedEntry, HistorySummary> Shape = new(
        entry => entry.Id,
        Summarize,
        summary => summary.Id);

    private readonly UserStoragePaths _paths;
    private readonly UserFileLocks _locks;
    private readonly DevConduitOptions _options;
    private readonly ILogger<RoutedTrafficStore> _logger;

    public RoutedTrafficStore(
        UserStoragePaths paths,
        UserFileLocks locks,
        IOptions<DevConduitOptions> options,
        ILogger<RoutedTrafficStore> logger)
    {
        _paths = paths;
        _locks = locks;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<HistoryPage> GetPageAsync(string userId, int skip, int take, string? query, CancellationToken cancellationToken)
    {
        var index = await LogFor(userId).ReadIndexAsync(cancellationToken).ConfigureAwait(false);
        return HistoryPaging.Page(index, skip, take, query);
    }

    public Task<RoutedEntry?> GetAsync(string userId, string entryId, CancellationToken cancellationToken) =>
        LogFor(userId).GetAsync(entryId, cancellationToken);

    public Task AppendAsync(string userId, RoutedEntry entry, CancellationToken cancellationToken) =>
        LogFor(userId).AppendAsync(entry, cancellationToken);

    public Task<bool> DeleteAsync(string userId, string entryId, CancellationToken cancellationToken) =>
        LogFor(userId).DeleteAsync(entryId, cancellationToken);

    public Task ClearAsync(string userId, CancellationToken cancellationToken) =>
        LogFor(userId).ClearAsync(cancellationToken);

    private JsonEntryLog<RoutedEntry, HistorySummary> LogFor(string userId) => new(
        _paths.RoutedDirectory(userId),
        _locks.For(userId, LockScope),
        _options.MaxRoutedHistoryEntries,
        Shape,
        _logger);

    private static HistorySummary Summarize(RoutedEntry entry) => new(
        entry.Id,
        entry.TimestampUtc,
        entry.Method,
        entry.Url,
        entry.StatusCode,
        entry.DurationMs,
        entry.RequestBytes,
        entry.ResponseBytes,
        entry.Error);
}
