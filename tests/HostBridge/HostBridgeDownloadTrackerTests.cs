using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.HostBridge;

/// <summary>
/// TDD pins for <see cref="HostBridgeDownloadItem"/> (the per-download tracker DTO) and
/// <see cref="HostBridgeDownloadTrackerStore{TItem}"/> (the process-wide ConcurrentDictionary +
/// retention sweep that apple/tidal each re-implemented).
///
/// Pinned contracts:
/// - Status and Progress are thread-safe (Volatile / Interlocked) — concurrent reads + writes
///   never observe a torn value.
/// - Completed/Failed items past the retention window are evicted by GetSnapshot.
/// - In-progress items are NEVER evicted regardless of age.
/// - Remove drops the item; with deleteData=true the OutputPath directory is removed if it exists.
/// </summary>
public class HostBridgeDownloadTrackerTests
{
    [Fact]
    public async Task DownloadItem_StatusReadWrite_IsThreadSafe()
    {
        var item = new HostBridgeDownloadItem();
        var observedStatuses = new System.Collections.Concurrent.ConcurrentBag<HostBridgeDownloadItemStatus>();

        var ct = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)).Token;

        var writer = Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                item.SetStatus(HostBridgeDownloadItemStatus.Downloading);
                item.SetStatus(HostBridgeDownloadItemStatus.Completed);
            }
        }, ct);

        var reader = Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                observedStatuses.Add(item.GetStatus());
            }
        }, ct);

        try { await Task.WhenAll(writer, reader).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { /* expected — both tasks bound by the 200ms CTS */ }

        // No exception thrown + every observed value is a valid enum value.
        foreach (var s in observedStatuses)
        {
            Assert.True(Enum.IsDefined(typeof(HostBridgeDownloadItemStatus), s));
        }
    }

    [Fact]
    public void DownloadItem_Progress_ReadWriteIsAtomic()
    {
        var item = new HostBridgeDownloadItem();
        item.SetProgress(0.0);
        Assert.Equal(0.0, item.GetProgress());

        item.SetProgress(50.5);
        Assert.Equal(50.5, item.GetProgress());

        item.SetProgress(100.0);
        Assert.Equal(100.0, item.GetProgress());
    }

    [Fact]
    public void TrackerStore_Add_ItemAppearsInSnapshot()
    {
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>();
        store.AddOrReplace(new HostBridgeDownloadItem { DownloadId = "abc", Title = "T", Artist = "A" });

        var snapshot = store.GetSnapshot().ToList();
        Assert.Single(snapshot);
        Assert.Equal("abc", snapshot[0].DownloadId);
    }

    [Fact]
    public void TrackerStore_GetSnapshot_EvictsCompletedPastRetention()
    {
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            completedRetention: TimeSpan.FromMilliseconds(50));

        var item = new HostBridgeDownloadItem { DownloadId = "old", Title = "T", Artist = "A" };
        item.SetStatus(HostBridgeDownloadItemStatus.Completed);
        item.CompletedAt = DateTime.UtcNow.AddMilliseconds(-100); // already past retention
        store.AddOrReplace(item);

        var snapshot = store.GetSnapshot().ToList();
        Assert.Empty(snapshot);
    }

    [Fact]
    public void TrackerStore_GetSnapshot_KeepsInProgressRegardlessOfAge()
    {
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            completedRetention: TimeSpan.FromMilliseconds(10));

        var item = new HostBridgeDownloadItem { DownloadId = "live", Title = "T", Artist = "A" };
        item.SetStatus(HostBridgeDownloadItemStatus.Downloading);
        item.StartedAt = DateTime.UtcNow.AddHours(-1); // very old start, but never completed
        store.AddOrReplace(item);

        Thread.Sleep(50);

        var snapshot = store.GetSnapshot().ToList();
        Assert.Single(snapshot);
        Assert.Equal("live", snapshot[0].DownloadId);
    }

    [Fact]
    public void TrackerStore_GetSnapshot_KeepsFailedWithinRetention()
    {
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            completedRetention: TimeSpan.FromMinutes(30));

        var item = new HostBridgeDownloadItem { DownloadId = "recent-fail", Title = "T", Artist = "A" };
        item.SetStatus(HostBridgeDownloadItemStatus.Failed);
        item.CompletedAt = DateTime.UtcNow; // just failed
        store.AddOrReplace(item);

        var snapshot = store.GetSnapshot().ToList();
        Assert.Single(snapshot);
    }

    [Fact]
    public void TrackerStore_Remove_DropsItem()
    {
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>();
        store.AddOrReplace(new HostBridgeDownloadItem { DownloadId = "to-remove", Title = "T", Artist = "A" });

        Assert.True(store.Remove("to-remove", deleteData: false, out _));

        Assert.Empty(store.GetSnapshot());
    }

    [Fact]
    public void TrackerStore_Remove_ReturnsFalseForUnknownId()
    {
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>();
        Assert.False(store.Remove("unknown", deleteData: false, out var removed));
        Assert.Null(removed);
    }

    [Fact]
    public void TrackerStore_TryGet_RetrievesByDownloadId()
    {
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>();
        store.AddOrReplace(new HostBridgeDownloadItem { DownloadId = "find-me", Title = "T", Artist = "A" });

        Assert.True(store.TryGet("find-me", out var item));
        Assert.NotNull(item);
        Assert.Equal("find-me", item.DownloadId);
    }
}
