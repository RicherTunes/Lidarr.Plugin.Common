using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.HostBridge;

public sealed class HostBridgeQueueIdentityAndCasTests
{
    private static HostBridgeDownloadTrackerStore<HostBridgeDownloadItem> Store(Func<DateTime>? clock = null) =>
        new(options: new HostBridgeQueueStoreOptions
        {
            ContractVersion = HostBridgeQueueContractVersion.AttemptV2,
            UtcNow = clock ?? (static () => DateTime.UtcNow),
        });

    [Fact]
    public void TryAddAttempt_TrimsAndKeysDownloadIdOrdinalIgnoreCase()
    {
        var store = Store();
        var result = store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "  AbC-123  " });

        Assert.True(result.Applied);
        Assert.Equal("AbC-123", result.Item!.DownloadId);
        Assert.True(store.TryGet("abc-123", out var lower));
        Assert.Same(result.Item, lower);
        Assert.Single(store.GetSnapshot());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void TryAddAttempt_RejectsBlankBeforeStorage(string id)
    {
        var store = Store();
        Assert.Throws<ArgumentException>(() =>
            store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = id }));
        Assert.Empty(store.GetSnapshot());
    }

    [Fact]
    public void TryAddAttempt_AssignsImmutableAttemptMetadata()
    {
        var instant = new DateTime(2026, 7, 11, 14, 0, 0, DateTimeKind.Utc);
        var result = Store(() => instant).TryAddAttempt(
            new HostBridgeDownloadItem { DownloadId = "attempt" });

        Assert.NotEqual(Guid.Empty, result.Item!.AttemptId);
        Assert.Equal(1, result.Item.Revision);
        Assert.Equal(HostBridgeDownloadAttemptState.Queued, result.Item.AttemptState);
        Assert.Equal(instant, result.Item.StateChangedAtUtc);
        Assert.Equal(result.Item.AttemptId, result.Current.AttemptId);
    }

    [Fact]
    public void TryAddAttempt_CaseVariantCannotReplaceExistingAttempt()
    {
        var store = Store();
        var first = store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "Release-ID" });
        var second = store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "release-id" });

        Assert.True(first.Applied);
        Assert.False(second.Applied);
        Assert.Equal(HostBridgeQueueResultCodes.Conflict, second.Code);
        Assert.Equal(first.Current, second.Current);
        Assert.Single(store.GetSnapshot().ToArray());
    }

    [Theory]
    [InlineData("TryAdd")]
    [InlineData("AddOrReplace")]
    [InlineData("TryAddAttempt")]
    public void AttemptV2_AllSuccessfulInsertionPathsInitializeAttemptMetadata(string insertionPath)
    {
        var instant = new DateTime(2026, 7, 11, 15, 0, 0, DateTimeKind.Utc);
        var store = Store(() => instant);
        var item = new HostBridgeDownloadItem { DownloadId = $"  {insertionPath}  " };

        switch (insertionPath)
        {
            case "TryAdd":
                Assert.True(store.TryAdd(item));
                break;
            case "AddOrReplace":
                store.AddOrReplace(item);
                break;
            case "TryAddAttempt":
                Assert.True(store.TryAddAttempt(item).Applied);
                break;
        }

        Assert.NotEqual(Guid.Empty, item.AttemptId);
        Assert.Equal(1, item.Revision);
        Assert.Equal(HostBridgeDownloadAttemptState.Queued, item.AttemptState);
        Assert.Equal(instant, item.StateChangedAtUtc);
        Assert.Equal(DateTimeKind.Utc, item.StateChangedAtUtc.Kind);
        Assert.Equal(insertionPath, item.DownloadId);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void TryAddAttempt_NormalizesClockToUtc(DateTimeKind kind)
    {
        var clockValue = new DateTime(2026, 7, 11, 15, 30, 0, kind);
        var expected = kind == DateTimeKind.Local
            ? clockValue.ToUniversalTime()
            : DateTime.SpecifyKind(clockValue, DateTimeKind.Utc);

        var item = Store(() => clockValue)
            .TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "clock" })
            .Item!;

        Assert.Equal(expected, item.StateChangedAtUtc);
        Assert.Equal(DateTimeKind.Utc, item.StateChangedAtUtc.Kind);
    }

    [Fact]
    public void DownloadItemDto_RoundTripsAttemptMetadata()
    {
        var instant = new DateTime(2026, 7, 11, 16, 0, 0, DateTimeKind.Utc);
        var original = Store(() => instant)
            .TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "round-trip" })
            .Item!;

        var restored = HostBridgeDownloadItemDto.FromItem(original).ToItem();

        Assert.Equal(original.AttemptId, restored.AttemptId);
        Assert.Equal(original.Revision, restored.Revision);
        Assert.Equal(original.AttemptState, restored.AttemptState);
        Assert.Equal(original.StateChangedAtUtc, restored.StateChangedAtUtc);
    }

    [Fact]
    public async Task TryAddAttempt_ConcurrentRemovalNeverThrowsDuringConflictLookup()
    {
        var store = Store();
        var exceptions = new ConcurrentQueue<Exception>();

        var adders = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                try
                {
                    store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "contended" });
                }
                catch (Exception ex)
                {
                    exceptions.Enqueue(ex);
                }
            }
        }));
        var removers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                try
                {
                    store.Remove("contended", deleteData: false, out _);
                }
                catch (Exception ex)
                {
                    exceptions.Enqueue(ex);
                }
            }
        }));

        await Task.WhenAll(adders.Concat(removers));

        Assert.Empty(exceptions);
    }
}
