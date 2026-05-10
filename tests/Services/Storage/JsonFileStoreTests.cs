// <copyright file="JsonFileStoreTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Storage;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Storage;

public class JsonFileStoreTests : IDisposable
{
    private readonly string _dir;

    public JsonFileStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lpc-jsonfilestore-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private string PathFor(string name = "store.json") => System.IO.Path.Combine(_dir, name);

    private sealed record Entry(string ReleaseId, string? ReleaseGroupId);

    [Fact]
    public async Task SetAsync_AndGetAsync_RoundTripValue()
    {
        var store = new JsonFileStore<string, Entry>(PathFor());
        await store.SetAsync("upc-1", new Entry("rel1", "rg1"));

        var loaded = await store.GetAsync("upc-1");
        Assert.NotNull(loaded);
        Assert.Equal("rel1", loaded!.ReleaseId);
        Assert.Equal("rg1", loaded.ReleaseGroupId);
    }

    [Fact]
    public async Task GetAsync_MissingKey_ReturnsNull()
    {
        var store = new JsonFileStore<string, Entry>(PathFor());
        var loaded = await store.GetAsync("does-not-exist");
        Assert.Null(loaded);
    }

    [Fact]
    public async Task SetAsync_PersistsAcrossInstances()
    {
        var path = PathFor();
        var first = new JsonFileStore<string, Entry>(path);
        await first.SetAsync("k", new Entry("r1", null));

        var second = new JsonFileStore<string, Entry>(path);
        var loaded = await second.GetAsync("k");

        Assert.NotNull(loaded);
        Assert.Equal("r1", loaded!.ReleaseId);
    }

    [Fact]
    public async Task RemoveAsync_RemovesEntry_AndReturnsTrue()
    {
        var store = new JsonFileStore<string, Entry>(PathFor());
        await store.SetAsync("k", new Entry("r1", null));

        var removed = await store.RemoveAsync("k");
        Assert.True(removed);

        var loaded = await store.GetAsync("k");
        Assert.Null(loaded);
    }

    [Fact]
    public async Task RemoveAsync_MissingKey_ReturnsFalse()
    {
        var store = new JsonFileStore<string, Entry>(PathFor());
        var removed = await store.RemoveAsync("nope");
        Assert.False(removed);
    }

    [Fact]
    public async Task ClearAsync_RemovesAllEntries()
    {
        var store = new JsonFileStore<string, Entry>(PathFor());
        await store.SetAsync("a", new Entry("ra", null));
        await store.SetAsync("b", new Entry("rb", null));

        await store.ClearAsync();

        Assert.Equal(0, store.Count);
        Assert.Null(await store.GetAsync("a"));
        Assert.Null(await store.GetAsync("b"));
    }

    [Fact]
    public async Task GetAsync_ExpiredEntry_ReturnsNull_WhenTtlExceeded()
    {
        var path = PathFor();
        var options = new JsonFileStoreOptions<string> { Ttl = TimeSpan.FromMilliseconds(100) };
        var store = new JsonFileStore<string, Entry>(path, options);
        await store.SetAsync("k", new Entry("r1", null));

        // Wait long enough for entry to expire
        await Task.Delay(250);

        var loaded = await store.GetAsync("k");
        Assert.Null(loaded);
    }

    [Fact]
    public async Task SetAsync_LruEviction_KeepsCapEnforced()
    {
        var options = new JsonFileStoreOptions<string> { MaxEntries = 3 };
        var store = new JsonFileStore<string, Entry>(PathFor(), options);

        await store.SetAsync("a", new Entry("ra", null));
        await Task.Delay(5);
        await store.SetAsync("b", new Entry("rb", null));
        await Task.Delay(5);
        await store.SetAsync("c", new Entry("rc", null));
        await Task.Delay(5);
        // This should push out "a" (oldest)
        await store.SetAsync("d", new Entry("rd", null));

        Assert.Equal(3, store.Count);
        Assert.Null(await store.GetAsync("a"));
        Assert.NotNull(await store.GetAsync("b"));
        Assert.NotNull(await store.GetAsync("c"));
        Assert.NotNull(await store.GetAsync("d"));
    }

    [Fact]
    public async Task EnumerateAsync_EnumeratesNonExpiredEntries_OrderedByTimestamp()
    {
        var store = new JsonFileStore<string, Entry>(PathFor());
        await store.SetAsync("first", new Entry("r1", null));
        await Task.Delay(5);
        await store.SetAsync("second", new Entry("r2", null));
        await Task.Delay(5);
        await store.SetAsync("third", new Entry("r3", null));

        var collected = new System.Collections.Generic.List<string>();
        await foreach (var pair in store.EnumerateAsync())
        {
            collected.Add(pair.Key);
        }

        Assert.Equal(new[] { "first", "second", "third" }, collected);
    }

    [Fact]
    public async Task KeyNormalizer_NormalizesAtBoundary()
    {
        var options = new JsonFileStoreOptions<string>
        {
            KeyNormalizer = k => (k ?? string.Empty).Trim().ToLowerInvariant(),
        };
        var store = new JsonFileStore<string, Entry>(PathFor(), options);

        await store.SetAsync("  ABC  ", new Entry("r1", null));

        var found = await store.GetAsync("abc");
        Assert.NotNull(found);
        Assert.Equal("r1", found!.ReleaseId);
    }

    [Fact]
    public async Task LoadInitial_CorruptedFile_StartsEmpty()
    {
        var path = PathFor();
        await File.WriteAllTextAsync(path, "not-valid-json{{{");

        var store = new JsonFileStore<string, Entry>(path);
        Assert.Equal(0, store.Count);

        // Should still be writable
        await store.SetAsync("k", new Entry("r", null));
        Assert.NotNull(await store.GetAsync("k"));
    }

    [Fact]
    public async Task AtomicSave_DoesNotLeaveTempFiles()
    {
        var path = PathFor();
        var store = new JsonFileStore<string, Entry>(path);
        await store.SetAsync("a", new Entry("r", null));
        await store.SetAsync("b", new Entry("r", null));
        await store.SetAsync("c", new Entry("r", null));

        var leftover = Directory.EnumerateFiles(_dir, "store.json.*.tmp").ToArray();
        Assert.Empty(leftover);
    }
}
