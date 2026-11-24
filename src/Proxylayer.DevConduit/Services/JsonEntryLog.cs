using System.Text.Json;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// How a particular kind of entry is identified and summarized. One instance per
/// store; the log itself is created per directory.
/// </summary>
/// <typeparam name="TEntry">The full entry written as its own file.</typeparam>
/// <typeparam name="TSummary">The lightweight projection kept in the index.</typeparam>
public sealed record JsonEntryLogShape<TEntry, TSummary>(
    Func<TEntry, string> EntryId,
    Func<TEntry, TSummary> Summarize,
    Func<TSummary, string> SummaryId);

/// <summary>
/// A capped, newest-first log of JSON entries in one directory: <c>index.json</c> holds
/// the summaries, each entry lives in <c>{id}.json</c>. Writes are serialized on the
/// supplied gate and applied atomically.
/// </summary>
public sealed class JsonEntryLog<TEntry, TSummary>
    where TEntry : class
    where TSummary : class
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate;
    private readonly int _maxEntries;
    private readonly JsonEntryLogShape<TEntry, TSummary> _shape;
    private readonly ILogger _logger;

    public JsonEntryLog(
        string directory,
        SemaphoreSlim gate,
        int maxEntries,
        JsonEntryLogShape<TEntry, TSummary> shape,
        ILogger logger)
    {
        _directory = directory;
        _gate = gate;
        _maxEntries = Math.Max(maxEntries, 1);
        _shape = shape;
        _logger = logger;
    }

    public string Directory => _directory;

    public string IndexFile => Path.Combine(_directory, "index.json");

    public async Task<IReadOnlyList<TSummary>> ReadIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            var index = await AtomicJsonFile
                .ReadAsync<List<TSummary>>(IndexFile, cancellationToken)
                .ConfigureAwait(false);
            return index ?? [];
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "The index at {IndexFile} is unreadable and will be rebuilt empty.", IndexFile);
            return [];
        }
    }

    public async Task<TEntry?> GetAsync(string entryId, CancellationToken cancellationToken)
    {
        if (!TryEntryFile(entryId, out var path))
        {
            return null;
        }

        return await AtomicJsonFile.ReadAsync<TEntry>(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the entry, inserts its summary at the head of the index and prunes the tail.</summary>
    public async Task AppendAsync(TEntry entry, CancellationToken cancellationToken)
    {
        var id = _shape.EntryId(entry);
        if (!TryEntryFile(id, out var path))
        {
            throw new ArgumentException("Entry id is not usable as a file name.", nameof(entry));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            await AtomicJsonFile.WriteAsync(path, entry, cancellationToken).ConfigureAwait(false);

            var index = (await ReadIndexAsync(cancellationToken).ConfigureAwait(false)).ToList();
            index.Insert(0, _shape.Summarize(entry));

            var pruned = new List<TSummary>();
            while (index.Count > _maxEntries)
            {
                pruned.Add(index[^1]);
                index.RemoveAt(index.Count - 1);
            }

            await AtomicJsonFile.WriteAsync(IndexFile, index, cancellationToken).ConfigureAwait(false);

            foreach (var old in pruned)
            {
                DeleteEntryFile(_shape.SummaryId(old));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string entryId, CancellationToken cancellationToken)
    {
        if (!TryEntryFile(entryId, out var path))
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = (await ReadIndexAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var removed = index.RemoveAll(item => string.Equals(_shape.SummaryId(item), entryId, StringComparison.Ordinal)) > 0;
            var fileExisted = File.Exists(path);

            if (removed)
            {
                await AtomicJsonFile.WriteAsync(IndexFile, index, cancellationToken).ConfigureAwait(false);
            }

            DeleteEntryFile(entryId);
            return removed || fileExisted;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (System.IO.Directory.Exists(_directory))
            {
                System.IO.Directory.Delete(_directory, recursive: true);
            }

            await AtomicJsonFile.WriteAsync(IndexFile, Array.Empty<TSummary>(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryEntryFile(string entryId, out string path)
    {
        if (!UserStoragePaths.IsSafeSegment(entryId) || string.Equals(entryId, "index", StringComparison.OrdinalIgnoreCase))
        {
            path = string.Empty;
            return false;
        }

        path = Path.Combine(_directory, entryId + ".json");
        return true;
    }

    private void DeleteEntryFile(string entryId)
    {
        if (!TryEntryFile(entryId, out var path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not delete stored entry {EntryId}.", entryId);
        }
    }
}
