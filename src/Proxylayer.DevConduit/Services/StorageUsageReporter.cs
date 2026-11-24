using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Models;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Measures what the signed-in user's stores occupy on disk. Full capture of routed
/// traffic is only safe if its cost is visible, so this is a first-class endpoint
/// rather than a diagnostic afterthought.
/// </summary>
public sealed class StorageUsageReporter
{
    private readonly UserStoragePaths _paths;
    private readonly IUserStore _store;
    private readonly DevConduitOptions _options;

    public StorageUsageReporter(UserStoragePaths paths, IUserStore store, IOptions<DevConduitOptions> options)
    {
        _paths = paths;
        _store = store;
        _options = options.Value;
    }

    public async Task<StorageUsage> MeasureAsync(string userId, CancellationToken cancellationToken)
    {
        var history = MeasureLog("history", _paths.HistoryDirectory(userId), _options.MaxHistoryEntries);
        var routed = MeasureLog("routed", _paths.RoutedDirectory(userId), _options.MaxRoutedHistoryEntries);

        var headerSetsFile = _paths.HeaderSetsFile(userId);
        var headerSets = await _store.GetHeaderSetsAsync(userId, cancellationToken).ConfigureAwait(false);
        var headers = new StoreUsage("headerSets", headerSetsFile, FileBytes(headerSetsFile), headerSets.Count, null);

        var stores = new[] { history, routed, headers };
        return new StorageUsage(_paths.DataRoot, stores.Sum(store => store.Bytes), stores);
    }

    private static StoreUsage MeasureLog(string name, string directory, int maxEntries)
    {
        long bytes = 0;
        var entries = 0;

        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                bytes += FileBytes(file);
                if (!string.Equals(Path.GetFileName(file), "index.json", StringComparison.OrdinalIgnoreCase))
                {
                    entries++;
                }
            }
        }

        return new StoreUsage(name, directory, bytes, entries, maxEntries);
    }

    private static long FileBytes(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
