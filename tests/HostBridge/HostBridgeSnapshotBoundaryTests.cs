using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.HostBridge;

public sealed class HostBridgeSnapshotBoundaryTests
{
    private static readonly DateTime Now = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("replacement")]
    [InlineData("removed")]
    [InlineData("reactivated")]
    [InlineData("cleared-time")]
    [InlineData("refreshed-time")]
    public void DeferredEviction_RevalidatesMembershipAndCurrentState(string change)
    {
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(completedRetention: TimeSpan.FromDays(1));
        var old = new ValueEqualItem { DownloadId = "same", CompletedAt = Now.AddDays(-2) };
        old.SetStatus(HostBridgeDownloadItemStatus.Completed);
        store.AddOrReplace(old);
        var observed = new KeyValuePair<string, HostBridgeDownloadItem>("same", old);
        HostBridgeDownloadItem? expected = old;
        switch (change)
        {
            case "replacement":
                expected = new ValueEqualItem { DownloadId = "same", CompletedAt = Now.AddDays(-3) };
                expected.SetStatus(HostBridgeDownloadItemStatus.Failed);
                Assert.True(old.Equals(expected)); // Value equality must not substitute for identity.
                store.AddOrReplace(expected);
                break;
            case "removed": Assert.True(store.Remove("same", false, out _)); expected = null; break;
            case "reactivated": old.SetStatus(HostBridgeDownloadItemStatus.Downloading); break;
            case "cleared-time": old.CompletedAt = null; break;
            case "refreshed-time": old.CompletedAt = Now; break;
        }

        var actual = store.CollectLegacyItem(observed, Now, out var evicted);

        Assert.False(evicted);
        Assert.Same(expected, actual);
        Assert.Equal(expected is not null, store.TryGet("same", out var retained));
        Assert.Same(expected, retained);
    }

    [Theory]
    [InlineData(HostBridgeDownloadItemStatus.Completed, -1, false)]
    [InlineData(HostBridgeDownloadItemStatus.Completed, 0, false)]
    [InlineData(HostBridgeDownloadItemStatus.Completed, 1, true)]
    [InlineData(HostBridgeDownloadItemStatus.Failed, 1, true)]
    [InlineData(HostBridgeDownloadItemStatus.Cancelled, 1, true)]
    [InlineData(HostBridgeDownloadItemStatus.Queued, 1, false)]
    [InlineData(HostBridgeDownloadItemStatus.Downloading, 1, false)]
    public void RetentionBoundary_RemainsStrictAndTerminalOnly(HostBridgeDownloadItemStatus status, long excessTicks, bool expectedEviction)
    {
        var retention = TimeSpan.FromDays(1);
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(completedRetention: retention);
        var item = new HostBridgeDownloadItem { DownloadId = "boundary", CompletedAt = Now - retention - TimeSpan.FromTicks(excessTicks) };
        item.SetStatus(status);
        store.AddOrReplace(item);

        var survivor = store.CollectLegacyItem(new("boundary", item), Now, out var evicted);

        Assert.Equal(expectedEviction, evicted);
        Assert.Equal(expectedEviction, survivor is null);
        Assert.Equal(!expectedEviction, store.TryGet("boundary", out _));
    }

    [Fact]
    public void AttemptV2_CannotUseLegacyRetentionEntryPoint()
    {
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(options: new HostBridgeQueueStoreOptions
        { ContractVersion = HostBridgeQueueContractVersion.AttemptV2 });
        var item = new HostBridgeDownloadItem { DownloadId = "v2" };
        Assert.True(store.TryAddAttempt(item).Applied);
        Assert.Throws<InvalidOperationException>(() => store.CollectLegacyItem(new("v2", item), Now, out _));
        Assert.Same(item, Assert.Single(store.GetSnapshot()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedSnapshot_PersistsAndReloadsWithoutChangingSchema(bool attemptV2)
    {
        var directory = Path.Combine(Path.GetTempPath(), "lpc-snapshot-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "queue.json");
        var warnings = new List<string>();
        var options = new HostBridgeQueueStoreOptions
        {
            ContractVersion = attemptV2 ? HostBridgeQueueContractVersion.AttemptV2 : HostBridgeQueueContractVersion.LegacyV1,
            UtcNow = () => Now
        };
        Directory.CreateDirectory(directory);
        try
        {
            var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(persistencePath: path, options: options, onWarn: warnings.Add);
            var item = new HostBridgeDownloadItem
            { DownloadId = "persisted", Title = "title", Artist = "artist", AlbumId = "album", StartedAt = Now, TotalSize = 1234 };
            if (attemptV2)
            {
                var added = store.TryAddAttempt(item);
                Assert.True(added.Applied);
                Assert.True(store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Failed).Applied);
            }
            else store.AddOrReplace(item);
            item.SetStatus(HostBridgeDownloadItemStatus.Failed);
            item.SetProgress(0.75);
            item.CompletedAt = DateTime.UtcNow;
            store.PersistSnapshot();
            var expected = HostBridgeDownloadItemDto.FromItem(item);
            var reloaded = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(persistencePath: path, options: options, onWarn: warnings.Add);
            var actual = HostBridgeDownloadItemDto.FromItem(Assert.Single(reloaded.GetSnapshot()));
            Assert.Empty(warnings);
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
            Assert.DoesNotContain("snapshotSync", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task TwoStoresSharingItems_CanCaptureWhileHoldingDifferentMutationLocks()
    {
        var left = new HostBridgeDownloadItem { DownloadId = "left" };
        var right = new HostBridgeDownloadItem { DownloadId = "right" };
        using var ready = new CountdownEvent(2);
        // The actual persistence projection can run while a store holds one item's
        // mutation lock. Capturing the other item must not require its MutationSync.
        Task Capture(HostBridgeDownloadItem held, HostBridgeDownloadItem other) => Task.Factory.StartNew(() =>
        {
            lock (held.MutationSync)
            {
                ready.Signal();
                Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
                // Check the lock separation directly before the cross-item capture, so
                // a regression to MutationSync fails rather than creating a deadlocked test.
                Assert.NotSame(other.MutationSync, other.SnapshotSync);
                var snapshot = HostBridgeDownloadItemDto.FromItem(other);
                Assert.Equal(other.DownloadId, snapshot.DownloadId);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await Task.WhenAll(Capture(left, right), Capture(right, left)).WaitAsync(TimeSpan.FromSeconds(20));
    }

    private sealed class ValueEqualItem : HostBridgeDownloadItem
    {
        public override bool Equals(object? obj) => obj is HostBridgeDownloadItem item && item.DownloadId == DownloadId;
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(DownloadId);
    }
}
