using System;
using System.Linq;
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
}
