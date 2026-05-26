// <copyright file="JsonFileStoreEdgeCaseTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Storage;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Storage;

/// <summary>
/// Edge-case coverage for <see cref="JsonFileStore{TKey, TValue}"/> beyond the happy-path tests
/// in <see cref="JsonFileStoreTests"/>: argument validation, comparer behavior, expired-entry
/// handling on enumerate/save, cancellation, empty-file load.
/// </summary>
[Trait("Category", "Unit")]
public sealed class JsonFileStoreEdgeCaseTests : IDisposable
{
    private readonly string _dir;

    public JsonFileStoreEdgeCaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lpc-jsonfilestore-edge-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string PathFor(string name = "store.json") => Path.Combine(_dir, name);

    private sealed record Entry(string ReleaseId);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_NullOrWhitespacePath_Throws(string? path)
    {
        Assert.Throws<ArgumentException>(() =>
            new JsonFileStore<string, Entry>(path!));
    }

    [Fact]
    public async Task GetAsync_NullKey_Throws()
    {
        var store = new JsonFileStore<string, Entry>(PathFor());
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.GetAsync(null!));
    }

    [Fact]
    public async Task SetAsync_NullKey_Throws()
    {
        var store = new JsonFileStore<string, Entry>(PathFor());
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await store.SetAsync(null!, new Entry("r")));
    }

    [Fact]
    public async Task RemoveAsync_NullKey_Throws()
    {
        var store = new JsonFileStore<string, Entry>(PathFor());
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.RemoveAsync(null!));
    }

    [Fact]
    public async Task KeyComparer_IgnoreCase_BindsAcrossCasings()
    {
        var options = new JsonFileStoreOptions<string>
        {
            KeyComparer = StringComparer.OrdinalIgnoreCase,
        };
        var store = new JsonFileStore<string, Entry>(PathFor(), options);
        await store.SetAsync("Abc", new Entry("r1"));

        var loaded = await store.GetAsync("ABC");
        Assert.NotNull(loaded);
        Assert.Equal("r1", loaded!.ReleaseId);
    }

    [Fact]
    public async Task KeyComparer_PersistsAcrossInstances()
    {
        var path = PathFor();
        var options = new JsonFileStoreOptions<string>
        {
            KeyComparer = StringComparer.OrdinalIgnoreCase,
        };
        var first = new JsonFileStore<string, Entry>(path, options);
        await first.SetAsync("KEY", new Entry("v"));

        // New instance, same comparer — must round-trip and lookup case-insensitively.
        var second = new JsonFileStore<string, Entry>(path, options);
        Assert.NotNull(await second.GetAsync("key"));
    }

    [Fact]
    public async Task LoadInitial_EmptyFile_StartsEmpty()
    {
        var path = PathFor();
        await File.WriteAllTextAsync(path, string.Empty);

        var store = new JsonFileStore<string, Entry>(path);
        Assert.Equal(0, store.Count);

        // Confirm still writable.
        await store.SetAsync("k", new Entry("r"));
        Assert.NotNull(await store.GetAsync("k"));
    }

    [Fact]
    public async Task LoadInitial_WhitespaceOnlyFile_StartsEmpty()
    {
        var path = PathFor();
        await File.WriteAllTextAsync(path, "   \n\t  \n");

        var store = new JsonFileStore<string, Entry>(path);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task LoadInitial_TopLevelNull_StartsEmpty()
    {
        var path = PathFor();
        await File.WriteAllTextAsync(path, "null");

        var store = new JsonFileStore<string, Entry>(path);
        Assert.Equal(0, store.Count);
    }

    // Generous timing margin for both TTL tests below:
    // - TTL = 300ms (was 75ms)
    // - Wait = 600ms (was 150ms)
    //
    // The previous values intermittently failed on shared CI runners under load
    // — the second SetAsync would occasionally land within the 75ms TTL window
    // because Task.Delay isn't a hard floor (it's a "wait AT LEAST this long"
    // contract, and the scheduler can return early under specific conditions).
    // Doubling the margin gives ample buffer without making the test wall-clock
    // expensive (~600ms each, ~1.2s total). A proper fix would inject a clock
    // abstraction into JsonFileStore so tests advance time deterministically,
    // but that's a bigger refactor than warranted for two tests.
    private static readonly TimeSpan TtlTestWindow = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan TtlTestWait = TimeSpan.FromMilliseconds(600);

    [Fact]
    public async Task EnumerateAsync_ExpiredEntries_AreOmitted()
    {
        var options = new JsonFileStoreOptions<string> { Ttl = TtlTestWindow };
        var store = new JsonFileStore<string, Entry>(PathFor(), options);

        await store.SetAsync("ephemeral", new Entry("e1"));
        await Task.Delay(TtlTestWait);
        await store.SetAsync("fresh", new Entry("f1"));

        // EnumerateAsync filters expired entries even before they're purged on save.
        var keys = new List<string>();
        await foreach (var pair in store.EnumerateAsync())
        {
            keys.Add(pair.Key);
        }

        Assert.Single(keys);
        Assert.Equal("fresh", keys[0]);
    }

    [Fact]
    public async Task SetAsync_PurgesExpiredEntriesOnSave()
    {
        var options = new JsonFileStoreOptions<string> { Ttl = TtlTestWindow };
        var path = PathFor();
        var store = new JsonFileStore<string, Entry>(path, options);

        await store.SetAsync("expires", new Entry("e"));
        await Task.Delay(TtlTestWait);
        await store.SetAsync("survivor", new Entry("s"));

        // Re-load: only "survivor" should persist (expired purged on save).
        var reloaded = new JsonFileStore<string, Entry>(path, options);
        Assert.Equal(1, reloaded.Count);
        Assert.NotNull(await reloaded.GetAsync("survivor"));
        Assert.Null(await reloaded.GetAsync("expires"));
    }

    [Fact]
    public async Task EnumerateAsync_Cancellation_Honored()
    {
        var store = new JsonFileStore<string, Entry>(PathFor());
        await store.SetAsync("a", new Entry("ra"));
        await store.SetAsync("b", new Entry("rb"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in store.EnumerateAsync(cts.Token))
            {
                // should never observe an item
            }
        });
    }

    [Fact]
    public async Task GetAsync_RespectsCancellation()
    {
        var store = new JsonFileStore<string, Entry>(PathFor());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.GetAsync("k", cts.Token));
    }

    [Fact]
    public async Task ClearAsync_PersistsEmptyState()
    {
        var path = PathFor();
        var first = new JsonFileStore<string, Entry>(path);
        await first.SetAsync("k", new Entry("r"));
        await first.ClearAsync();

        var second = new JsonFileStore<string, Entry>(path);
        Assert.Equal(0, second.Count);
        Assert.Null(await second.GetAsync("k"));
    }

    [Fact]
    public async Task SetAsync_UpdateExistingKey_RefreshesTimestamp_NotEvictedFirst()
    {
        var options = new JsonFileStoreOptions<string> { MaxEntries = 2 };
        var store = new JsonFileStore<string, Entry>(PathFor(), options);

        await store.SetAsync("a", new Entry("ra"));
        await Task.Delay(10);
        await store.SetAsync("b", new Entry("rb"));
        await Task.Delay(10);
        // Refreshing "a" should make it newest; inserting "c" must evict "b" (the oldest).
        await store.SetAsync("a", new Entry("ra2"));
        await Task.Delay(10);
        await store.SetAsync("c", new Entry("rc"));

        Assert.Equal(2, store.Count);
        Assert.NotNull(await store.GetAsync("a"));
        Assert.NotNull(await store.GetAsync("c"));
        Assert.Null(await store.GetAsync("b"));
    }

    [Fact]
    public void Count_OnFreshStore_IsZero()
    {
        var store = new JsonFileStore<string, Entry>(PathFor());
        Assert.Equal(0, store.Count);
    }
}
