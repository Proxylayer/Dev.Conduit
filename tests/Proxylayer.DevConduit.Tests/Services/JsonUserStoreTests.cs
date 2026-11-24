using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Models;
using Proxylayer.DevConduit.Services;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Services;

/// <summary>
/// Per-user JSON storage: the history cap, the body cap, and the promise that a saved
/// header set keeps the value the developer typed.
/// </summary>
public sealed class JsonUserStoreTests
{
    private const string UserId = "user-1";

    [Fact]
    public async Task AppendHistoryAsync_KeepsTheNewestEntriesAndDeletesTheFilesOfThePruned()
    {
        using var temp = new TempDirectory();
        var store = Store(temp, options => options.MaxHistoryEntries = 3);
        var paths = Paths(temp);

        var ids = new List<string>();
        for (var index = 0; index < 5; index++)
        {
            var entry = Entry($"entry{index}");
            ids.Add(entry.Id);
            await store.AppendHistoryAsync(UserId, entry, CancellationToken.None);
        }

        var page = await store.GetHistoryAsync(UserId, 0, 50, null, CancellationToken.None);

        Assert.Equal(3, page.Total);
        Assert.Equal(new[] { "entry4", "entry3", "entry2" }, page.Items.Select(item => item.Id));

        foreach (var evicted in ids.Take(2))
        {
            Assert.Null(await store.GetHistoryEntryAsync(UserId, evicted, CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(paths.HistoryDirectory(UserId), evicted + ".json")));
        }

        foreach (var kept in ids.Skip(2))
        {
            Assert.True(File.Exists(Path.Combine(paths.HistoryDirectory(UserId), kept + ".json")));
        }
    }

    [Fact]
    public async Task AppendHistoryAsync_TreatsACapOfZeroAsOneEntry()
    {
        using var temp = new TempDirectory();
        var store = Store(temp, options => options.MaxHistoryEntries = 0);

        await store.AppendHistoryAsync(UserId, Entry("first"), CancellationToken.None);
        await store.AppendHistoryAsync(UserId, Entry("second"), CancellationToken.None);

        var page = await store.GetHistoryAsync(UserId, 0, 50, null, CancellationToken.None);

        Assert.Equal(1, page.Total);
        Assert.Equal("second", page.Items[0].Id);
    }

    [Fact]
    public async Task AppendHistoryAsync_LeavesABodyExactlyAtTheCapIntact()
    {
        using var temp = new TempDirectory();
        var store = Store(temp, options => options.MaxStoredBodyBytes = 1024);
        var body = new string('a', 1024);

        var stored = await store.AppendHistoryAsync(UserId, Entry("at-cap", body), CancellationToken.None);

        Assert.Equal(body, stored.Request.Body);
        Assert.False(stored.RequestBodyTruncated);
        Assert.Equal(body, stored.Response!.Body);
        Assert.False(stored.Response.BodyTruncated);
    }

    [Fact]
    public async Task AppendHistoryAsync_TruncatesAndFlagsABodyOneByteOverTheCap()
    {
        using var temp = new TempDirectory();
        var store = Store(temp, options => options.MaxStoredBodyBytes = 1024);
        var body = new string('a', 1025);

        var stored = await store.AppendHistoryAsync(UserId, Entry("over-cap", body), CancellationToken.None);

        Assert.Equal(1024, Encoding.UTF8.GetByteCount(stored.Request.Body!));
        Assert.True(stored.RequestBodyTruncated);
        Assert.Equal(1024, Encoding.UTF8.GetByteCount(stored.Response!.Body!));
        Assert.True(stored.Response.BodyTruncated);
    }

    [Fact]
    public async Task AppendHistoryAsync_NeverCutsAMultiByteCharacterInHalf()
    {
        using var temp = new TempDirectory();
        var store = Store(temp, options => options.MaxStoredBodyBytes = 1024);

        // 1023 single-byte characters followed by a two-byte one: the cap falls inside it.
        var body = new string('a', 1023) + 'é';

        var stored = await store.AppendHistoryAsync(UserId, Entry("split", body), CancellationToken.None);

        Assert.Equal(new string('a', 1023), stored.Request.Body);
        Assert.True(stored.RequestBodyTruncated);
        Assert.DoesNotContain('�', stored.Request.Body!);
    }

    [Fact]
    public async Task AppendHistoryAsync_RaisesAnUnreasonablySmallCapToTheFloor()
    {
        using var temp = new TempDirectory();
        var store = Store(temp, options => options.MaxStoredBodyBytes = 10);
        var body = new string('a', 1024);

        var stored = await store.AppendHistoryAsync(UserId, Entry("floor", body), CancellationToken.None);

        Assert.Equal(body, stored.Request.Body);
        Assert.False(stored.RequestBodyTruncated);
    }

    [Fact]
    public async Task AppendHistoryAsync_LeavesTheCallersOwnCopyOfTheEntryUntruncated()
    {
        using var temp = new TempDirectory();
        var store = Store(temp, options => options.MaxStoredBodyBytes = 1024);
        var body = new string('a', 4096);
        var live = Entry("live", body);

        var stored = await store.AppendHistoryAsync(UserId, live, CancellationToken.None);

        Assert.Equal(body, live.Request.Body);
        Assert.Equal(body, live.Response!.Body);
        Assert.False(live.RequestBodyTruncated);
        Assert.False(live.Response.BodyTruncated);
        Assert.NotEqual(body, stored.Request.Body);
    }

    [Fact]
    public async Task AppendHistoryAsync_KeepsAnAlreadyFlaggedTruncationFlagged()
    {
        using var temp = new TempDirectory();
        var store = Store(temp, options => options.MaxStoredBodyBytes = 1024);
        var entry = Entry("flagged", "short") with { RequestBodyTruncated = true };

        var stored = await store.AppendHistoryAsync(UserId, entry, CancellationToken.None);

        Assert.True(stored.RequestBodyTruncated);
        Assert.Equal("short", stored.Request.Body);
    }

    [Fact]
    public async Task AppendHistoryAsync_RoundTripsTheStoredEntry()
    {
        using var temp = new TempDirectory();
        var store = Store(temp);

        await store.AppendHistoryAsync(UserId, Entry("round-trip", "payload"), CancellationToken.None);
        var loaded = await store.GetHistoryEntryAsync(UserId, "round-trip", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("payload", loaded!.Request.Body);
        Assert.Equal("GET", loaded.Request.Method);
    }

    [Fact]
    public async Task AppendHistoryAsync_RefusesAnEntryIdThatIsNotUsableAsAFileName()
    {
        using var temp = new TempDirectory();
        var store = Store(temp);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.AppendHistoryAsync(UserId, Entry("../escape"), CancellationToken.None));
    }

    [Fact]
    public async Task Writes_LeaveNoTemporaryFilesBehind()
    {
        using var temp = new TempDirectory();
        var store = Store(temp, options => options.MaxHistoryEntries = 2);

        for (var index = 0; index < 4; index++)
        {
            await store.AppendHistoryAsync(UserId, Entry($"entry{index}"), CancellationToken.None);
        }

        await store.CreateHeaderSetAsync(
            UserId,
            new SavedHeaderSetInput("set", false, [new HeaderItem("Accept", "application/json")]),
            CancellationToken.None);

        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task GetHistoryEntryAsync_ReturnsNullForAnIdThatCouldEscapeTheDirectory()
    {
        using var temp = new TempDirectory();
        var store = Store(temp);

        Assert.Null(await store.GetHistoryEntryAsync(UserId, "../../secrets", CancellationToken.None));
        Assert.Null(await store.GetHistoryEntryAsync(UserId, "index", CancellationToken.None));
    }

    [Fact]
    public async Task ClearHistoryAsync_EmptiesTheIndexAndTheEntryFiles()
    {
        using var temp = new TempDirectory();
        var store = Store(temp);

        await store.AppendHistoryAsync(UserId, Entry("one"), CancellationToken.None);
        await store.ClearHistoryAsync(UserId, CancellationToken.None);

        var page = await store.GetHistoryAsync(UserId, 0, 50, null, CancellationToken.None);

        Assert.Equal(0, page.Total);
        Assert.Null(await store.GetHistoryEntryAsync(UserId, "one", CancellationToken.None));
    }

    [Fact]
    public async Task CreateHeaderSetAsync_KeepsSecretValuesInTheSavedSet()
    {
        using var temp = new TempDirectory();
        var store = Store(temp);

        var created = await store.CreateHeaderSetAsync(
            UserId,
            new SavedHeaderSetInput(
                "  api  ",
                true,
                [
                    new HeaderItem("  Authorization  ", "Bearer real-token", IsSecret: true),
                    new HeaderItem("Accept", "application/json"),
                    new HeaderItem("   ", "dropped")
                ]),
            CancellationToken.None);

        var reloaded = Assert.Single(await store.GetHeaderSetsAsync(UserId, CancellationToken.None));

        Assert.Equal("api", created.Name);
        Assert.Equal(2, reloaded.Headers.Count);
        Assert.Equal("Authorization", reloaded.Headers[0].Name);
        Assert.Equal("Bearer real-token", reloaded.Headers[0].Value);
        Assert.NotEqual(SecretHeaderPolicy.RedactedValue, reloaded.Headers[0].Value);
    }

    [Fact]
    public async Task CreateHeaderSetAsync_KeepsTheLocalSharedSecretValueInTheSavedSet()
    {
        using var temp = new TempDirectory();
        var store = Store(temp);

        await store.CreateHeaderSetAsync(
            UserId,
            new SavedHeaderSetInput(
                "local",
                false,
                [new HeaderItem(DevConduitHeaderNames.LocalSecret, "the-shared-secret")]),
            CancellationToken.None);

        var reloaded = Assert.Single(await store.GetHeaderSetsAsync(UserId, CancellationToken.None));

        // The header is redacted on the way into history; the saved set keeps it usable.
        Assert.Equal("the-shared-secret", Assert.Single(reloaded.Headers).Value);
    }

    [Fact]
    public async Task GetHeaderSetsAsync_IsEmptyBeforeAnythingIsSaved()
    {
        using var temp = new TempDirectory();

        Assert.Empty(await Store(temp).GetHeaderSetsAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteHeaderSetAsync_RemovesOnlyTheNamedSet()
    {
        using var temp = new TempDirectory();
        var store = Store(temp);

        var first = await store.CreateHeaderSetAsync(UserId, new SavedHeaderSetInput("one", false, []), CancellationToken.None);
        await store.CreateHeaderSetAsync(UserId, new SavedHeaderSetInput("two", false, []), CancellationToken.None);

        Assert.True(await store.DeleteHeaderSetAsync(UserId, first.Id, CancellationToken.None));
        Assert.False(await store.DeleteHeaderSetAsync(UserId, first.Id, CancellationToken.None));

        var remaining = Assert.Single(await store.GetHeaderSetsAsync(UserId, CancellationToken.None));
        Assert.Equal("two", remaining.Name);
    }

    [Fact]
    public async Task Storage_StaysUnderTheUsersOwnDirectory()
    {
        using var temp = new TempDirectory();
        var store = Store(temp);

        await store.AppendHistoryAsync(UserId, Entry("one"), CancellationToken.None);

        var expected = Path.Combine(temp.Path, "users", UserId);
        foreach (var file in Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories))
        {
            Assert.StartsWith(expected, file, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static UserStoragePaths Paths(TempDirectory temp) =>
        new(TestOptions.Create(options => options.DataRoot = temp.Path));

    private static JsonUserStore Store(TempDirectory temp, Action<DevConduitOptions>? configure = null)
    {
        var options = TestOptions.Create(settings =>
        {
            settings.DataRoot = temp.Path;
            configure?.Invoke(settings);
        });

        return new JsonUserStore(
            new UserStoragePaths(options),
            new UserFileLocks(),
            options,
            NullLogger<JsonUserStore>.Instance);
    }

    private static HistoryEntry Entry(string id, string body = "body") => new(
        id,
        DateTimeOffset.UtcNow,
        new ProxyRequest("GET", "https://example.com/api", [new HeaderItem("Accept", "application/json")], body),
        new ProxyResponse(200, "OK", "1.1", [], 12, body.Length, body, false, false, "text/plain"),
        null,
        null,
        body.Length,
        false);
}
