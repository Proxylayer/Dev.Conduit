using System.Text;
using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Models;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Stores console history and saved header sets as JSON files under the configured data root.
/// Writes for a given user are serialized and applied atomically.
/// </summary>
public sealed class JsonUserStore : IUserStore
{
    private const string LockScope = "console";

    private static readonly JsonEntryLogShape<HistoryEntry, HistorySummary> Shape = new(
        entry => entry.Id,
        Summarize,
        summary => summary.Id);

    private readonly UserStoragePaths _paths;
    private readonly UserFileLocks _locks;
    private readonly DevConduitOptions _options;
    private readonly ILogger<JsonUserStore> _logger;

    public JsonUserStore(
        UserStoragePaths paths,
        UserFileLocks locks,
        IOptions<DevConduitOptions> options,
        ILogger<JsonUserStore> logger)
    {
        _paths = paths;
        _locks = locks;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<HistoryPage> GetHistoryAsync(string userId, int skip, int take, string? query, CancellationToken cancellationToken)
    {
        var index = await LogFor(userId).ReadIndexAsync(cancellationToken).ConfigureAwait(false);
        return HistoryPaging.Page(index, skip, take, query);
    }

    public Task<HistoryEntry?> GetHistoryEntryAsync(string userId, string entryId, CancellationToken cancellationToken) =>
        LogFor(userId).GetAsync(entryId, cancellationToken);

    public async Task<HistoryEntry> AppendHistoryAsync(string userId, HistoryEntry entry, CancellationToken cancellationToken)
    {
        var stored = ApplyBodyCaps(entry);
        await LogFor(userId).AppendAsync(stored, cancellationToken).ConfigureAwait(false);
        return stored;
    }

    public Task<bool> DeleteHistoryEntryAsync(string userId, string entryId, CancellationToken cancellationToken) =>
        LogFor(userId).DeleteAsync(entryId, cancellationToken);

    public Task ClearHistoryAsync(string userId, CancellationToken cancellationToken) =>
        LogFor(userId).ClearAsync(cancellationToken);

    public async Task<IReadOnlyList<SavedHeaderSet>> GetHeaderSetsAsync(string userId, CancellationToken cancellationToken)
    {
        var sets = await AtomicJsonFile
            .ReadAsync<List<SavedHeaderSet>>(_paths.HeaderSetsFile(userId), cancellationToken)
            .ConfigureAwait(false);
        return sets ?? [];
    }

    public async Task<SavedHeaderSet> CreateHeaderSetAsync(string userId, SavedHeaderSetInput input, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var created = new SavedHeaderSet(
            Guid.NewGuid().ToString("n"),
            input.Name.Trim(),
            input.ApplyByDefault,
            Normalize(input.Headers),
            now,
            now);

        var gate = GateFor(userId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sets = (await GetHeaderSetsAsync(userId, cancellationToken).ConfigureAwait(false)).ToList();
            sets.Add(created);
            await AtomicJsonFile
                .WriteAsync(_paths.HeaderSetsFile(userId), sets, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        return created;
    }

    public async Task<SavedHeaderSet?> ReplaceHeaderSetAsync(string userId, string setId, SavedHeaderSetInput input, CancellationToken cancellationToken)
    {
        var gate = GateFor(userId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sets = (await GetHeaderSetsAsync(userId, cancellationToken).ConfigureAwait(false)).ToList();
            var index = sets.FindIndex(set => string.Equals(set.Id, setId, StringComparison.Ordinal));
            if (index < 0)
            {
                return null;
            }

            var replacement = sets[index] with
            {
                Name = input.Name.Trim(),
                ApplyByDefault = input.ApplyByDefault,
                Headers = Normalize(input.Headers),
                Updated = DateTimeOffset.UtcNow
            };

            sets[index] = replacement;
            await AtomicJsonFile
                .WriteAsync(_paths.HeaderSetsFile(userId), sets, cancellationToken)
                .ConfigureAwait(false);
            return replacement;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> DeleteHeaderSetAsync(string userId, string setId, CancellationToken cancellationToken)
    {
        var gate = GateFor(userId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sets = (await GetHeaderSetsAsync(userId, cancellationToken).ConfigureAwait(false)).ToList();
            if (sets.RemoveAll(set => string.Equals(set.Id, setId, StringComparison.Ordinal)) == 0)
            {
                return false;
            }

            await AtomicJsonFile
                .WriteAsync(_paths.HeaderSetsFile(userId), sets, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private JsonEntryLog<HistoryEntry, HistorySummary> LogFor(string userId) => new(
        _paths.HistoryDirectory(userId),
        GateFor(userId),
        _options.MaxHistoryEntries,
        Shape,
        _logger);

    private SemaphoreSlim GateFor(string userId) => _locks.For(userId, LockScope);

    private static IReadOnlyList<HeaderItem> Normalize(IReadOnlyList<HeaderItem>? headers) =>
        (headers ?? [])
        .Where(header => !string.IsNullOrWhiteSpace(header.Name))
        .Select(header => header with { Name = header.Name.Trim() })
        .ToArray();

    private static HistorySummary Summarize(HistoryEntry entry) => new(
        entry.Id,
        entry.TimestampUtc,
        entry.Request.Method,
        entry.Request.Url,
        entry.Response?.StatusCode,
        entry.Response?.DurationMs ?? 0,
        entry.RequestBytes,
        entry.Response?.BodyBytes ?? 0,
        entry.Error);

    private HistoryEntry ApplyBodyCaps(HistoryEntry entry)
    {
        var (requestBody, requestTruncated) = Cap(entry.Request.Body);
        var result = entry with
        {
            Request = entry.Request with { Body = requestBody },
            RequestBodyTruncated = entry.RequestBodyTruncated || requestTruncated
        };

        if (result.Response is { } response)
        {
            var (responseBody, responseTruncated) = Cap(response.Body);
            result = result with
            {
                Response = response with
                {
                    Body = responseBody,
                    BodyTruncated = response.BodyTruncated || responseTruncated
                }
            };
        }

        return result;
    }

    private (string? Body, bool Truncated) Cap(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return (body, false);
        }

        var limit = Math.Max(_options.MaxStoredBodyBytes, 1024);
        if (Encoding.UTF8.GetByteCount(body) <= limit)
        {
            return (body, false);
        }

        var bytes = Encoding.UTF8.GetBytes(body);
        var length = limit;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
        {
            length--;
        }

        return (Encoding.UTF8.GetString(bytes, 0, length), true);
    }
}
