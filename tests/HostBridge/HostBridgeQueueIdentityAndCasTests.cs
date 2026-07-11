using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

    [Theory]
    [InlineData(HostBridgeDownloadAttemptState.Queued, HostBridgeDownloadAttemptState.Preparing)]
    [InlineData(HostBridgeDownloadAttemptState.Preparing, HostBridgeDownloadAttemptState.Downloading)]
    [InlineData(HostBridgeDownloadAttemptState.Downloading, HostBridgeDownloadAttemptState.Paused)]
    [InlineData(HostBridgeDownloadAttemptState.Paused, HostBridgeDownloadAttemptState.Downloading)]
    [InlineData(HostBridgeDownloadAttemptState.Downloading, HostBridgeDownloadAttemptState.Finalizing)]
    [InlineData(HostBridgeDownloadAttemptState.Finalizing, HostBridgeDownloadAttemptState.CompletedImportable)]
    [InlineData(HostBridgeDownloadAttemptState.Queued, HostBridgeDownloadAttemptState.Failed)]
    [InlineData(HostBridgeDownloadAttemptState.Downloading, HostBridgeDownloadAttemptState.Cancelling)]
    [InlineData(HostBridgeDownloadAttemptState.Cancelling, HostBridgeDownloadAttemptState.Cancelled)]
    public void StateMachine_AllowsCanonicalEdges(
        HostBridgeDownloadAttemptState from,
        HostBridgeDownloadAttemptState to) =>
        Assert.True(HostBridgeQueueStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(HostBridgeDownloadAttemptState.Queued, HostBridgeDownloadAttemptState.CompletedImportable)]
    [InlineData(HostBridgeDownloadAttemptState.CompletedImportable, HostBridgeDownloadAttemptState.Queued)]
    [InlineData(HostBridgeDownloadAttemptState.Failed, HostBridgeDownloadAttemptState.Downloading)]
    [InlineData(HostBridgeDownloadAttemptState.Cancelled, HostBridgeDownloadAttemptState.Queued)]
    [InlineData(HostBridgeDownloadAttemptState.Cancelling, HostBridgeDownloadAttemptState.Failed)]
    public void StateMachine_RejectsSkippedAndTerminalRegressionEdges(
        HostBridgeDownloadAttemptState from,
        HostBridgeDownloadAttemptState to) =>
        Assert.False(HostBridgeQueueStateMachine.CanTransition(from, to));

    [Fact]
    public void TryTransition_UsesFullCasAndMonotonicTimestamp()
    {
        var times = new Queue<DateTime>(new[]
        {
            new DateTime(2026, 7, 11, 15, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 11, 14, 0, 0, DateTimeKind.Utc),
        });
        var store = Store(() => times.Dequeue());
        var added = store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "Case-Key" });
        var initialTimestamp = added.Item!.StateChangedAtUtc;

        var moved = store.TryTransition(
            new HostBridgeQueueMutationKey("case-key", added.Current.AttemptId, 1),
            HostBridgeDownloadAttemptState.Preparing);
        var stale = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Failed);

        Assert.True(moved.Applied);
        Assert.Equal(2, moved.Current.Revision);
        Assert.Equal(initialTimestamp.AddTicks(1), moved.Item!.StateChangedAtUtc);
        Assert.False(stale.Applied);
        Assert.Equal(HostBridgeQueueResultCodes.Conflict, stale.Code);
        Assert.Equal(HostBridgeDownloadAttemptState.Preparing, stale.Item!.AttemptState);
        Assert.Equal(moved.Current, stale.Current);
    }

    [Fact]
    public void TryTransition_StalledAndBackwardClockRemainStrictlyMonotonic()
    {
        var instant = new DateTime(2026, 7, 11, 15, 0, 0, DateTimeKind.Utc);
        var times = new Queue<DateTime>(new[] { instant, instant, instant.AddHours(-1) });
        var store = Store(() => times.Dequeue());
        var added = store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "clock" });

        var preparing = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Preparing);
        var preparingTimestamp = preparing.Item!.StateChangedAtUtc;
        var downloading = store.TryTransition(preparing.Current, HostBridgeDownloadAttemptState.Downloading);

        Assert.Equal(instant.AddTicks(1), preparingTimestamp);
        Assert.Equal(instant.AddTicks(2), downloading.Item!.StateChangedAtUtc);
    }

    [Fact]
    public void TryTransition_IllegalEdgeDoesNotMutateMetadata()
    {
        var store = Store();
        var added = store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "illegal" });
        var before = Metadata(added.Item!);

        var result = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.CompletedImportable);

        Assert.False(result.Applied);
        Assert.Equal(HostBridgeQueueResultCodes.IllegalTransition, result.Code);
        Assert.Equal(added.Current, result.Current);
        Assert.Equal(before, Metadata(result.Item!));
    }

    [Fact]
    public void TryTransition_StaleAttemptCannotOverwriteReplacement()
    {
        var store = Store();
        var old = store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "same" });
        Assert.True(store.Remove("same", false, out _));
        var current = store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "SAME" });

        var stale = store.TryTransition(old.Current, HostBridgeDownloadAttemptState.Preparing);

        Assert.False(stale.Applied);
        Assert.Equal(HostBridgeQueueResultCodes.Conflict, stale.Code);
        Assert.Equal(current.Current, stale.Current);
        Assert.Same(current.Item, stale.Item);
    }

    [Fact]
    public async Task TryTransition_ConcurrentSameRevision_OnlyOneWins()
    {
        var store = Store();
        var added = store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "race" });
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Preparing);
        })).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(calls);

        Assert.Single(results, result => result.Applied);
        Assert.Equal(31, results.Count(result => result.Code == HostBridgeQueueResultCodes.Conflict));
        Assert.Equal(2, added.Item!.Revision);
        Assert.Equal(HostBridgeDownloadAttemptState.Preparing, added.Item.AttemptState);
    }

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

    [Theory]
    [InlineData("TryAdd")]
    [InlineData("AddOrReplace")]
    [InlineData("TryAddAttempt")]
    public void AttemptV2_ReaddingStoredInstanceDoesNotRewriteAttemptMetadata(string insertionPath)
    {
        var clockCalls = 0;
        var instant = new DateTime(2026, 7, 11, 17, 0, 0, DateTimeKind.Utc);
        var store = Store(() => instant.AddTicks(Interlocked.Increment(ref clockCalls)));
        var item = new HostBridgeDownloadItem { DownloadId = "same-instance" };
        Assert.True(store.TryAddAttempt(item).Applied);
        var captured = Metadata(item);

        switch (insertionPath)
        {
            case "TryAdd":
                Assert.False(store.TryAdd(item));
                break;
            case "AddOrReplace":
                Assert.Throws<InvalidOperationException>(() => store.AddOrReplace(item));
                break;
            case "TryAddAttempt":
                Assert.False(store.TryAddAttempt(item).Applied);
                break;
        }

        Assert.Equal(captured, Metadata(item));
        Assert.Equal(1, clockCalls);
    }

    [Fact]
    public async Task AttemptV2_ConcurrentSameInstanceInsertionInitializesMetadataExactlyOnce()
    {
        var clockCalls = 0;
        var instant = new DateTime(2026, 7, 11, 18, 0, 0, DateTimeKind.Utc);
        var store = Store(() => instant.AddTicks(Interlocked.Increment(ref clockCalls)));
        var item = new HostBridgeDownloadItem { DownloadId = "  concurrent-instance  " };
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = new ConcurrentBag<(Guid, long, HostBridgeDownloadAttemptState, DateTime)>();
        var successes = 0;

        var calls = Enumerable.Range(0, 60).Select(index => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                switch (index % 3)
                {
                    case 0:
                        if (store.TryAdd(item)) Interlocked.Increment(ref successes);
                        break;
                    case 1:
                        store.AddOrReplace(item);
                        Interlocked.Increment(ref successes);
                        break;
                    case 2:
                        if (store.TryAddAttempt(item).Applied) Interlocked.Increment(ref successes);
                        break;
                }
            }
            catch (InvalidOperationException)
            {
                // Expected for losing AddOrReplace calls.
            }
            finally
            {
                snapshots.Add(Metadata(item));
            }
        })).ToArray();

        start.SetResult();
        await Task.WhenAll(calls);

        Assert.Equal(1, successes);
        Assert.Equal(1, clockCalls);
        var captured = Metadata(item);
        Assert.NotEqual(Guid.Empty, captured.Item1);
        Assert.Equal(1, captured.Item2);
        Assert.Equal(HostBridgeDownloadAttemptState.Queued, captured.Item3);
        Assert.Equal(DateTimeKind.Utc, captured.Item4.Kind);
        Assert.All(snapshots, snapshot => Assert.Equal(captured, snapshot));
        Assert.Equal("concurrent-instance", item.DownloadId);
    }

    private static (Guid, long, HostBridgeDownloadAttemptState, DateTime) Metadata(
        HostBridgeDownloadItem item) =>
        (item.AttemptId, item.Revision, item.AttemptState, item.StateChangedAtUtc);
}
