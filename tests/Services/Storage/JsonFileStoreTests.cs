// <copyright file="JsonFileStoreTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Storage;
using Microsoft.Extensions.Time.Testing;
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
    public async Task SetManyAsync_PersistsAcrossInstances_WithSingleSave()
    {
        var path = PathFor();
        var saveCount = 0;
        var first = new JsonFileStore<string, Entry>(
            path,
            new JsonFileStoreOptions<string>
            {
                SaveCompleted = () => saveCount++,
            });

        await first.SetManyAsync(new[]
        {
            new KeyValuePair<string, Entry>("a", new Entry("ra", null)),
            new KeyValuePair<string, Entry>("b", new Entry("rb", "group-b")),
            new KeyValuePair<string, Entry>("c", new Entry("rc", null)),
        });

        Assert.Equal(1, saveCount);

        var second = new JsonFileStore<string, Entry>(path);
        Assert.Equal("ra", (await second.GetAsync("a"))!.ReleaseId);
        Assert.Equal("group-b", (await second.GetAsync("b"))!.ReleaseGroupId);
        Assert.Equal("rc", (await second.GetAsync("c"))!.ReleaseId);
    }

    [Fact]
    public async Task SetManyAsync_EmptyBatch_DoesNotPersistOrSave()
    {
        var saveCount = 0;
        var store = new JsonFileStore<string, Entry>(
            PathFor(),
            new JsonFileStoreOptions<string>
            {
                SaveCompleted = () => saveCount++,
            });

        await store.SetManyAsync(Array.Empty<KeyValuePair<string, Entry>>());

        Assert.Equal(0, saveCount);
        Assert.Equal(0, store.Count);
        Assert.False(File.Exists(PathFor()));
    }

    [Fact]
    public async Task SetManyAsync_NullCollection_Throws()
    {
        var store = new JsonFileStore<string, Entry>(PathFor());

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await store.SetManyAsync(null!));
    }

    [Fact]
    public async Task SetManyAsync_NullKey_ThrowsWithoutPartialMutation()
    {
        var store = new JsonFileStore<string, Entry>(PathFor());

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.SetManyAsync(new[]
            {
                new KeyValuePair<string, Entry>("valid", new Entry("rv", null)),
                new KeyValuePair<string, Entry>(null!, new Entry("bad", null)),
            }));

        Assert.Equal(0, store.Count);
        Assert.Null(await store.GetAsync("valid"));
        Assert.False(File.Exists(PathFor()));
    }

    [Fact]
    public async Task SetManyAsync_NormalizesKeysAndLastDuplicateWins()
    {
        var options = new JsonFileStoreOptions<string>
        {
            KeyNormalizer = k => (k ?? string.Empty).Trim().ToLowerInvariant(),
        };
        var store = new JsonFileStore<string, Entry>(PathFor(), options);

        await store.SetManyAsync(new[]
        {
            new KeyValuePair<string, Entry>("  ABC  ", new Entry("old", null)),
            new KeyValuePair<string, Entry>("abc", new Entry("new", "winner")),
        });

        Assert.Equal(1, store.Count);
        var loaded = await store.GetAsync(" ABC ");
        Assert.NotNull(loaded);
        Assert.Equal("new", loaded!.ReleaseId);
        Assert.Equal("winner", loaded.ReleaseGroupId);
    }

    [Fact]
    public async Task SetManyAsync_EnforcesMaxEntriesAfterWholeBatch()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new JsonFileStore<string, Entry>(
            PathFor(),
            new JsonFileStoreOptions<string>
            {
                MaxEntries = 3,
                Clock = clock,
            });

        await store.SetAsync("old", new Entry("old-release", null));
        clock.Advance(TimeSpan.FromMinutes(1));

        await store.SetManyAsync(new[]
        {
            new KeyValuePair<string, Entry>("a", new Entry("ra", null)),
            new KeyValuePair<string, Entry>("b", new Entry("rb", null)),
            new KeyValuePair<string, Entry>("c", new Entry("rc", null)),
        });

        Assert.Equal(3, store.Count);
        Assert.Null(await store.GetAsync("old"));
        Assert.NotNull(await store.GetAsync("a"));
        Assert.NotNull(await store.GetAsync("b"));
        Assert.NotNull(await store.GetAsync("c"));
    }

    [Fact]
    public async Task SetManyAsync_WhenBatchExceedsMaxEntries_KeepsNewestBatchEntries()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new JsonFileStore<string, Entry>(
            PathFor(),
            new JsonFileStoreOptions<string>
            {
                MaxEntries = 2,
                Clock = clock,
            });

        await store.SetManyAsync(new[]
        {
            new KeyValuePair<string, Entry>("a", new Entry("ra", null)),
            new KeyValuePair<string, Entry>("b", new Entry("rb", null)),
            new KeyValuePair<string, Entry>("c", new Entry("rc", null)),
            new KeyValuePair<string, Entry>("d", new Entry("rd", null)),
        });

        Assert.Equal(2, store.Count);
        Assert.Null(await store.GetAsync("a"));
        Assert.Null(await store.GetAsync("b"));
        Assert.NotNull(await store.GetAsync("c"));
        Assert.NotNull(await store.GetAsync("d"));
    }

    [Fact]
    public async Task SetManyAsync_PurgesExpiredEntriesOnSave()
    {
        var path = PathFor();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = new JsonFileStoreOptions<string>
        {
            Ttl = TimeSpan.FromMinutes(5),
            Clock = clock,
        };
        var store = new JsonFileStore<string, Entry>(path, options);

        await store.SetAsync("expired", new Entry("old", null));
        clock.Advance(TimeSpan.FromMinutes(6));

        await store.SetManyAsync(new[]
        {
            new KeyValuePair<string, Entry>("fresh", new Entry("new", null)),
        });

        var reloaded = new JsonFileStore<string, Entry>(path, options);
        Assert.Equal(1, reloaded.Count);
        Assert.Null(await reloaded.GetAsync("expired"));
        Assert.NotNull(await reloaded.GetAsync("fresh"));
    }

    [Fact]
    public async Task SetManyAsync_WhenSaveFailsAndThrowEnabled_RollsBackWholeBatch()
    {
        var options = new JsonFileStoreOptions<string>
        {
            ThrowOnSaveFailure = true,
        };
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };
        serializerOptions.Converters.Add(new ThrowingEntryWriteConverter());
        var store = new JsonFileStore<string, Entry>(PathFor(), options, serializerOptions);

        await Assert.ThrowsAsync<JsonException>(async () =>
            await store.SetManyAsync(new[]
            {
                new KeyValuePair<string, Entry>("a", new Entry("ra", null)),
                new KeyValuePair<string, Entry>("b", new Entry("rb", null)),
            }));

        Assert.Equal(0, store.Count);
        Assert.Null(await store.GetAsync("a"));
        Assert.Null(await store.GetAsync("b"));
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

    private sealed class ThrowingEntryWriteConverter : JsonConverter<Entry>
    {
        public override Entry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            return new Entry(
                root.GetProperty("releaseId").GetString() ?? string.Empty,
                root.TryGetProperty("releaseGroupId", out var group) ? group.GetString() : null);
        }

        public override void Write(Utf8JsonWriter writer, Entry value, JsonSerializerOptions options)
        {
            throw new JsonException("intentional test save failure");
        }
    }
}
