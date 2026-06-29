using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.HostBridge;

/// <summary>
/// TDD pins for the persistence layer of <see cref="HostBridgeDownloadTrackerStore{TItem}"/>.
/// Each test follows the STRICT TDD mandate: watch it FAIL first, then implement.
///
/// Critical contract under test:
/// A download that finishes but has not yet been imported into Lidarr must survive a
/// Lidarr restart. Prior to this feature, the in-memory store was lost on restart, leaving
/// "Completed-pending-import" entries orphaned in Lidarr's queue.
/// </summary>
public sealed class HostBridgeDownloadTrackerPersistenceTests : IDisposable
{
    private readonly string _tempDir;

    public HostBridgeDownloadTrackerPersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hbdts-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private string TempFile(string name = "tracker.json") => Path.Combine(_tempDir, name);

    private sealed class CustomDownloadItem : HostBridgeDownloadItem
    {
        public string ProviderAlbumId { get; init; } = string.Empty;
    }

    private static CustomDownloadItem RestoreCustomItem(HostBridgeDownloadItemDto dto)
    {
        var item = new CustomDownloadItem
        {
            DownloadId      = dto.DownloadId,
            AlbumId         = dto.AlbumId,
            Title           = dto.Title,
            Artist          = dto.Artist,
            OutputPath      = dto.OutputPath,
            StartedAt       = dto.StartedAt,
            CompletedAt     = dto.CompletedAt,
            TotalSize       = dto.TotalSize,
            ProviderAlbumId = "provider:" + dto.AlbumId,
        };
        item.SetStatus(dto.Status);
        item.SetProgress(dto.Progress);
        return item;
    }

    // ─── Round-trip / survives-restart ───────────────────────────────────────

    [Fact]
    public void PersistentStore_SurvivesRestart_CompletedPendingImport()
    {
        var path = TempFile();
        var storeA = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            persistencePath: path);

        var item = new HostBridgeDownloadItem
        {
            DownloadId = "dl-abc123",
            AlbumId    = "alb-1",
            Title      = "Test Album",
            Artist     = "Test Artist",
            OutputPath = "/music/Test Artist/Test Album",
        };
        item.SetStatus(HostBridgeDownloadItemStatus.Completed);
        item.SetProgress(100.0);
        item.CompletedAt = DateTime.UtcNow;
        storeA.AddOrReplace(item);

        // Simulate restart: new store on same path
        var storeB = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            persistencePath: path);

        Assert.True(storeB.TryGet("dl-abc123", out var loaded));
        Assert.NotNull(loaded);
        Assert.Equal("dl-abc123", loaded!.DownloadId);
        Assert.Equal("Test Album",  loaded.Title);
        Assert.Equal("Test Artist", loaded.Artist);
        Assert.Equal("/music/Test Artist/Test Album", loaded.OutputPath);
        Assert.Equal(HostBridgeDownloadItemStatus.Completed, loaded.GetStatus());
        Assert.Equal(100.0, loaded.GetProgress());
        Assert.True(loaded.CompletedAt.HasValue);
    }

    [Fact]
    public void PersistentStore_SurvivesRestart_InProgressItem()
    {
        var path   = TempFile();
        var storeA = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            persistencePath: path);

        var item = new HostBridgeDownloadItem
        {
            DownloadId = "dl-inprogress",
            AlbumId    = "alb-2",
            Title      = "Flying Album",
            Artist     = "Inflight Artist",
            OutputPath = "/music/tmp/Flying Album",
        };
        item.SetStatus(HostBridgeDownloadItemStatus.Downloading);
        item.SetProgress(42.5);
        storeA.AddOrReplace(item);

        var storeB = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            persistencePath: path);

        Assert.True(storeB.TryGet("dl-inprogress", out var loaded));
        Assert.NotNull(loaded);
        Assert.Equal(HostBridgeDownloadItemStatus.Downloading, loaded!.GetStatus());
        Assert.Equal(42.5, loaded.GetProgress(), precision: 6);
    }

    [Fact]
    public void PersistentStore_SurvivesRestart_MultipleItems()
    {
        var path   = TempFile();
        var storeA = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            persistencePath: path);

        for (int i = 0; i < 5; i++)
        {
            var it = new HostBridgeDownloadItem
            {
                DownloadId = $"dl-{i}",
                AlbumId    = $"alb-{i}",
                Title      = $"Album {i}",
                Artist     = "Artist",
            };
            it.SetStatus(HostBridgeDownloadItemStatus.Completed);
            it.CompletedAt = DateTime.UtcNow;
            storeA.AddOrReplace(it);
        }

        var storeB   = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            persistencePath: path);
        var snapshot = storeB.GetSnapshot().ToList();

        Assert.Equal(5, snapshot.Count);
        for (int i = 0; i < 5; i++)
            Assert.Contains(snapshot, x => x.DownloadId == $"dl-{i}");
    }

    // ─── No persistence path → backward-compatible in-memory behavior ────────

    [Fact]
    public void NoPersistencePath_BehavesAsInMemory_NoFileCreated()
    {
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>();
        var item  = new HostBridgeDownloadItem { DownloadId = "in-memory", Title = "T", Artist = "A" };
        store.AddOrReplace(item);

        Assert.Empty(Directory.GetFiles(_tempDir));
        Assert.True(store.TryGet("in-memory", out _));
    }

    // ─── Corruption tolerance ────────────────────────────────────────────────

    [Fact]
    public void CorruptFile_LoadsEmpty_DoesNotThrow()
    {
        var path = TempFile();
        File.WriteAllText(path, "this is not JSON }{garbage]]]");

        var ex = Record.Exception(() =>
        {
            var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
                persistencePath: path);
            Assert.Empty(store.GetSnapshot());
        });

        Assert.Null(ex);
    }

    [Fact]
    public void CorruptFile_InvokesWarnCallback()
    {
        var path = TempFile();
        File.WriteAllText(path, "{{broken json");

        var warnings = new List<string>();
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            persistencePath: path,
            onWarn: msg => warnings.Add(msg));

        Assert.NotEmpty(warnings);
        Assert.Empty(store.GetSnapshot());
    }

    [Fact]
    public void EmptyFile_LoadsEmpty_DoesNotThrow()
    {
        var path = TempFile();
        File.WriteAllText(path, string.Empty);

        var ex = Record.Exception(() =>
        {
            var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
                persistencePath: path);
            Assert.Empty(store.GetSnapshot());
        });

        Assert.Null(ex);
    }

    // ─── Bounded growth / eviction ───────────────────────────────────────────

    [Fact]
    public void ExpiredItems_NotLoadedAfterRestart()
    {
        var path   = TempFile();
        var storeA = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            completedRetention: TimeSpan.FromMinutes(30),
            persistencePath:    path);

        var expired = new HostBridgeDownloadItem
            { DownloadId = "expired", Title = "Old Album", Artist = "Old Artist" };
        expired.SetStatus(HostBridgeDownloadItemStatus.Completed);
        expired.CompletedAt = DateTime.UtcNow.AddMinutes(-60);   // 60 min > 30 min retention
        storeA.AddOrReplace(expired);

        var fresh = new HostBridgeDownloadItem
            { DownloadId = "fresh", Title = "Fresh Album", Artist = "Fresh Artist" };
        fresh.SetStatus(HostBridgeDownloadItemStatus.Completed);
        fresh.CompletedAt = DateTime.UtcNow;
        storeA.AddOrReplace(fresh);

        var storeB = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            completedRetention: TimeSpan.FromMinutes(30),
            persistencePath:    path);

        Assert.False(storeB.TryGet("expired", out _), "Expired item must not be loaded");
        Assert.True(storeB.TryGet("fresh",   out _), "Fresh item must be loaded");
    }

    [Fact]
    public void GetSnapshot_Eviction_PersistsUpdatedState()
    {
        var path  = TempFile();
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            completedRetention: TimeSpan.FromMilliseconds(10),
            persistencePath:    path);

        var item = new HostBridgeDownloadItem
            { DownloadId = "to-evict", Title = "T", Artist = "A" };
        item.SetStatus(HostBridgeDownloadItemStatus.Completed);
        item.CompletedAt = DateTime.UtcNow.AddMilliseconds(-50);
        store.AddOrReplace(item);

        _ = store.GetSnapshot().ToList();   // triggers eviction

        var store2 = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            completedRetention: TimeSpan.FromMilliseconds(10),
            persistencePath:    path);

        Assert.False(store2.TryGet("to-evict", out _));
    }

    // ─── Atomic write ────────────────────────────────────────────────────────

    [Fact]
    public void AtomicWrite_NoTempFileLingers()
    {
        var path  = TempFile();
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            persistencePath: path);

        store.AddOrReplace(new HostBridgeDownloadItem { DownloadId = "x", Title = "T", Artist = "A" });

        Assert.False(File.Exists(path + ".tmp"));
        Assert.True(File.Exists(path));
    }

    // ─── Concurrency ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentAddRemove_FileRemainsValidJson()
    {
        var path  = TempFile();
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            persistencePath: path);

        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            for (int j = 0; j < 10; j++)
            {
                var id = $"dl-{i}-{j}";
                store.AddOrReplace(new HostBridgeDownloadItem
                    { DownloadId = id, Title = $"T{i}{j}", Artist = "A" });
                store.Remove(id, deleteData: false, out _);
            }
        }));

        await Task.WhenAll(tasks);

        if (File.Exists(path))
        {
            var content = File.ReadAllText(path);
            var ex = Record.Exception(() =>
                System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(content));
            Assert.Null(ex);
        }
    }

    // ─── ForPlugin factory ───────────────────────────────────────────────────

    [Fact]
    public void ForPlugin_CreatesStoreWithResolvedPath()
    {
        Environment.SetEnvironmentVariable("LIDARR_PLUGIN_CONFIG", _tempDir);
        try
        {
            var store = HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>.ForPlugin("TestPlugin");

            store.AddOrReplace(new HostBridgeDownloadItem
                { DownloadId = "pfx", Title = "T", Artist = "A" });

            var expectedPath = Path.Combine(_tempDir, "TestPlugin", "download-tracker.json");
            Assert.True(File.Exists(expectedPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIDARR_PLUGIN_CONFIG", null);
        }
    }

    // ─── Remove persists updated state ───────────────────────────────────────

    [Fact]
    public void Remove_PersistsUpdatedState()
    {
        var path  = TempFile();
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            persistencePath: path);

        store.AddOrReplace(new HostBridgeDownloadItem { DownloadId = "a", Title = "T", Artist = "A" });
        store.AddOrReplace(new HostBridgeDownloadItem { DownloadId = "b", Title = "T", Artist = "A" });
        store.Remove("a", deleteData: false, out _);

        var store2 = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            persistencePath: path);

        Assert.False(store2.TryGet("a", out _));
        Assert.True(store2.TryGet("b",  out _));
    }

    // ─── Subclass restore contract ──────────────────────────────────────────

    [Fact]
    public void SubclassStore_WithCustomFactory_SurvivesRestart()
    {
        var path = TempFile();
        var storeA = new HostBridgeDownloadTrackerStore<CustomDownloadItem>(
            persistencePath: path);

        var original = new CustomDownloadItem
        {
            DownloadId      = "custom-1",
            AlbumId         = "album-custom",
            Title           = "Custom Album",
            Artist          = "Custom Artist",
            OutputPath      = "/music/custom",
            TotalSize       = 987654,
            ProviderAlbumId = "provider:before-restart",
        };
        original.SetStatus(HostBridgeDownloadItemStatus.Failed);
        original.SetProgress(87.25);
        original.CompletedAt = DateTime.UtcNow;
        storeA.AddOrReplace(original);

        var storeB = new HostBridgeDownloadTrackerStore<CustomDownloadItem>(
            persistencePath: path,
            itemFactory: RestoreCustomItem);

        Assert.True(storeB.TryGet("custom-1", out var loaded));
        Assert.NotNull(loaded);
        Assert.Equal("provider:album-custom", loaded!.ProviderAlbumId);
        Assert.Equal(HostBridgeDownloadItemStatus.Failed, loaded.GetStatus());
        Assert.Equal(87.25, loaded.GetProgress(), precision: 6);
        Assert.Equal(987654, loaded.TotalSize);
    }

    [Fact]
    public void SubclassStore_WithoutCustomFactory_SkipsPersistedEntryAndWarns()
    {
        var path = TempFile();
        var storeA = new HostBridgeDownloadTrackerStore<CustomDownloadItem>(
            persistencePath: path);
        storeA.AddOrReplace(new CustomDownloadItem
        {
            DownloadId      = "custom-missing-factory",
            AlbumId         = "album-custom",
            Title           = "Custom Album",
            Artist          = "Custom Artist",
            ProviderAlbumId = "provider:before-restart",
        });

        var warnings = new List<string>();
        var storeB = new HostBridgeDownloadTrackerStore<CustomDownloadItem>(
            persistencePath: path,
            onWarn: warnings.Add);

        Assert.Empty(storeB.GetSnapshot());
        Assert.Contains(warnings, warning => warning.Contains("itemFactory", StringComparison.Ordinal));
    }

    // ─── HostBridgeDownloadItemDto explicit round-trip ───────────────────────

    [Fact]
    public void Dto_FromItem_RoundTrips_AllFields()
    {
        var original = new HostBridgeDownloadItem
        {
            DownloadId = "dto-test",
            AlbumId    = "alb-dto",
            Title      = "DTO Album",
            Artist     = "DTO Artist",
            OutputPath = "/music/dto",
            TotalSize  = 123_456_789L,
            StartedAt  = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc),
        };
        original.SetStatus(HostBridgeDownloadItemStatus.Completed);
        original.SetProgress(99.9);
        original.CompletedAt = new DateTime(2026, 6, 1, 10, 30, 0, DateTimeKind.Utc);

        var dto      = HostBridgeDownloadItemDto.FromItem(original);
        var restored = dto.ToItem();

        Assert.Equal(original.DownloadId,    restored.DownloadId);
        Assert.Equal(original.AlbumId,       restored.AlbumId);
        Assert.Equal(original.Title,         restored.Title);
        Assert.Equal(original.Artist,        restored.Artist);
        Assert.Equal(original.OutputPath,    restored.OutputPath);
        Assert.Equal(original.TotalSize,     restored.TotalSize);
        Assert.Equal(original.StartedAt,     restored.StartedAt);
        Assert.Equal(original.GetStatus(),   restored.GetStatus());
        Assert.Equal(original.GetProgress(), restored.GetProgress(), precision: 6);
        Assert.Equal(original.CompletedAt,   restored.CompletedAt);
    }
}
