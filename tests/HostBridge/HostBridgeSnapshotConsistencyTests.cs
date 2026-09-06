using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.HostBridge;

public sealed class HostBridgeSnapshotConsistencyTests
{
    private static readonly DateTime Epoch = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task DtoCapture_NeverCombinesDifferentAttemptTransitions()
    {
        var item = new HostBridgeDownloadItem { DownloadId = "snapshot" };
        var attemptId = Guid.NewGuid();
        item.RestoreAttempt(attemptId, 1, HostBridgeDownloadAttemptState.Queued, Epoch);

        await ObserveDuringWritesAsync(
            () =>
            {
                for (var revision = 2; revision <= 100_001; revision++)
                    item.ApplyTransition(revision % 2 == 0
                        ? HostBridgeDownloadAttemptState.Downloading
                        : HostBridgeDownloadAttemptState.Preparing, Epoch);
            },
            () =>
            {
                var captured = HostBridgeDownloadItemDto.FromItem(item);
                var expectedState = captured.Revision == 1 ? HostBridgeDownloadAttemptState.Queued
                    : captured.Revision % 2 == 0 ? HostBridgeDownloadAttemptState.Downloading
                    : HostBridgeDownloadAttemptState.Preparing;
                return captured.AttemptId == attemptId && captured.AttemptState == expectedState &&
                    captured.StateChangedAtUtc.Ticks == Epoch.Ticks + captured.Revision - 1
                    ? null
                    : $"Torn DTO: revision={captured.Revision}, state={captured.AttemptState}, ticks={captured.StateChangedAtUtc.Ticks}";
            });
    }

    [Fact]
    public async Task MutationKey_NeverCombinesAttemptIdentityAndRevisionFromDifferentRestores()
    {
        var first = new Guid("11111111-1111-1111-1111-111111111111");
        var second = new Guid("22222222-2222-2222-2222-222222222222");
        var item = new HostBridgeDownloadItem { DownloadId = "snapshot" };
        item.RestoreAttempt(first, 11, HostBridgeDownloadAttemptState.Queued, Epoch);

        await ObserveDuringWritesAsync(
            () =>
            {
                for (var i = 0; i < 100_000; i++)
                {
                    item.RestoreAttempt(second, 22, HostBridgeDownloadAttemptState.Preparing, Epoch.AddTicks(1));
                    item.RestoreAttempt(first, 11, HostBridgeDownloadAttemptState.Queued, Epoch);
                }
            },
            () =>
            {
                var key = item.MutationKey();
                return key.DownloadId == "snapshot" &&
                    ((key.AttemptId == first && key.Revision == 11) || (key.AttemptId == second && key.Revision == 22))
                    ? null : $"Torn mutation key: {key}";
            });
    }

    [Fact]
    public async Task LegacyRetention_NullableCompletionChangesNeverThrowOrEvictRecentItem()
    {
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(completedRetention: TimeSpan.FromDays(1));
        var item = new HostBridgeDownloadItem { DownloadId = "recent", CompletedAt = DateTime.UtcNow };
        item.SetStatus(HostBridgeDownloadItemStatus.Completed);
        store.AddOrReplace(item);
        var recent = DateTime.UtcNow;

        await ObserveDuringWritesAsync(
            () =>
            {
                for (var i = 0; i < 100_000; i++)
                {
                    item.CompletedAt = null;
                    item.CompletedAt = recent;
                }
            },
            () =>
            {
                try
                {
                    _ = store.GetSnapshot();
                    return store.TryGet("recent", out var found) && ReferenceEquals(found, item)
                        ? null : "A recent or undated item was evicted.";
                }
                catch (Exception ex) { return $"Snapshot threw {ex.GetType().Name}: {ex.Message}"; }
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyRetention_StaleExpiredEntryCannotRemoveItsReplacement(bool valueEquality)
    {
        var store = new HostBridgeDownloadTrackerStore<EqualByIdItem>(completedRetention: TimeSpan.FromDays(1));
        var expired = new EqualByIdItem(valueEquality) { DownloadId = "same-id", CompletedAt = Epoch };
        expired.SetStatus(HostBridgeDownloadItemStatus.Completed);
        var replacement = new EqualByIdItem(valueEquality) { DownloadId = "same-id" };
        replacement.SetStatus(HostBridgeDownloadItemStatus.Downloading);
        store.AddOrReplace(replacement);
        var missingReplacement = 0;

        await ObserveDuringWritesAsync(
            () =>
            {
                for (var i = 0; i < 100_000; i++)
                {
                    store.AddOrReplace(expired);
                    store.AddOrReplace(replacement);
                    if (!store.TryGet("same-id", out var found) || !ReferenceEquals(found, replacement))
                        Interlocked.Increment(ref missingReplacement);
                }
            },
            () => { _ = store.GetSnapshot(); return null; });

        Assert.Equal(0, missingReplacement);
        Assert.True(store.TryGet("same-id", out var retained));
        Assert.Same(replacement, retained);
    }

    [Fact]
    public void SnapshotContracts_PreserveLiveMembershipAndDetachedDtoBehavior()
    {
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>();
        var item = new HostBridgeDownloadItem
        {
            DownloadId = "round-trip", AlbumId = "album", Artist = "artist", Title = "title", OutputPath = "output",
            StartedAt = DateTime.SpecifyKind(Epoch, DateTimeKind.Unspecified), TotalSize = long.MaxValue, CompletedAt = DateTime.UtcNow
        };
        item.SetStatus(HostBridgeDownloadItemStatus.Failed);
        item.SetProgress(-0.0);
        item.RestoreAttempt(Guid.NewGuid(), 7, HostBridgeDownloadAttemptState.Preparing, Epoch.AddTicks(6));
        store.AddOrReplace(item);
        Assert.Same(item, Assert.Single(store.GetSnapshot()));
        var dto = HostBridgeDownloadItemDto.FromItem(item);
        item.TotalSize = 0;
        item.SetProgress(0.5);
        Assert.Equal(long.MaxValue, dto.TotalSize);
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(dto.Progress));
        var restored = dto.ToItem();
        Assert.Equal(dto.DownloadId, restored.DownloadId);
        Assert.Equal(dto.AlbumId, restored.AlbumId);
        Assert.Equal(dto.Title, restored.Title);
        Assert.Equal(dto.Artist, restored.Artist);
        Assert.Equal(dto.OutputPath, restored.OutputPath);
        Assert.Equal(dto.StartedAt.Kind, restored.StartedAt.Kind);
        Assert.Equal(dto.StartedAt, restored.StartedAt);
        Assert.Equal(dto.CompletedAt, restored.CompletedAt);
        Assert.Equal(dto.AttemptId, restored.AttemptId);
        Assert.Equal(dto.Revision, restored.Revision);
        Assert.Equal(dto.AttemptState, restored.AttemptState);
        Assert.Equal(dto.StateChangedAtUtc, restored.StateChangedAtUtc);
        Assert.Equal(dto.Status, restored.GetStatus());
        Assert.Equal(long.MaxValue, restored.TotalSize);
    }

    // These are bounded concurrency witnesses, not promises of a particular scheduling order.
    // Both workers start together; the reader must observe before the writer proceeds.
    // Every observed state has a precise oracle, and every worker is joined before returning.
    private static async Task ObserveDuringWritesAsync(Action write, Func<string?> observe)
    {
        using var start = new ManualResetEventSlim();
        using var firstObservation = new ManualResetEventSlim();
        using var stopped = new CancellationTokenSource(Watchdog);
        var errors = new ConcurrentQueue<string>();
        var complete = 0;
        var observations = 0;
        var reader = Task.Factory.StartNew(() =>
        {
            start.Wait(stopped.Token);
            do
            {
                try
                {
                    var error = observe();
                    if (error is not null && errors.Count < 8) errors.Enqueue(error);
                }
                catch (Exception ex) { if (errors.Count < 8) errors.Enqueue(ex.ToString()); }
                Interlocked.Increment(ref observations);
                firstObservation.Set();
            } while (Volatile.Read(ref complete) == 0 && !stopped.IsCancellationRequested);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var writer = Task.Factory.StartNew(() =>
        {
            try { start.Wait(stopped.Token); firstObservation.Wait(stopped.Token); write(); }
            finally { Volatile.Write(ref complete, 1); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        start.Set();
        try { await Task.WhenAll(reader, writer).WaitAsync(Watchdog); }
        finally
        {
            stopped.Cancel();
            start.Set();
            firstObservation.Set();
            await Task.WhenAll(reader, writer).WaitAsync(Watchdog);
        }
        Assert.True(observations > 0);
        Assert.True(errors.IsEmpty, string.Join(Environment.NewLine, errors));
    }

    private sealed class EqualByIdItem(bool valueEquality) : HostBridgeDownloadItem
    {
        public override bool Equals(object? obj) => valueEquality
            ? obj is EqualByIdItem other && other.DownloadId == DownloadId
            : ReferenceEquals(this, obj);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(DownloadId);
    }
}
