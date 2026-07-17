using System;
using System.IO;
using System.Linq;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

/// <summary>
/// Bounded terminal-item eviction for AttemptV2 (5B item 3). Terminal items are auto-evicted from
/// the snapshot by TTL and by a count high-water (oldest-terminal-first); non-terminal items are
/// never evicted so restart-recovery evidence is preserved.
/// </summary>
public sealed class HostBridgeQueueEvictionV2Tests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "queue-evict-" + Guid.NewGuid().ToString("N"));

    private string TempFile()
    {
        Directory.CreateDirectory(_root);
        return Path.Combine(_root, "queue.json");
    }

    private static HostBridgeDownloadItem Item(string id) => new() { DownloadId = id };

    private HostBridgeDownloadTrackerStore<HostBridgeDownloadItem> Store(
        Func<DateTime> clock, int highWater = 5000, TimeSpan? retention = null) =>
        new(
            completedRetention: retention ?? TimeSpan.FromMinutes(30),
            persistencePath: TempFile(),
            options: new HostBridgeQueueStoreOptions
            {
                ContractVersion = HostBridgeQueueContractVersion.AttemptV2,
                UtcNow = clock,
                TerminalRetentionHighWater = highWater,
            });

    [Fact]
    public void TerminalItemPastRetention_IsRetained_WhenUnderHighWater()
    {
        // AttemptV2 does NOT TTL-evict terminal items — they are UI/audit evidence. Only the count
        // high-water bounds them (see CountHighWater test). This pins the deliberate divergence from
        // LegacyV1's time-boxed sweep (see OldCompletedImportable_SurvivesSnapshotRetentionSweep).
        var now = new DateTime(2026, 07, 16, 12, 0, 0, DateTimeKind.Utc);
        var store = Store(() => now, highWater: 5000, retention: TimeSpan.FromMinutes(1));
        var added = store.TryAddAttempt(Item("done"));
        store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Failed);

        now = now.AddDays(365);

        Assert.Single(store.GetSnapshot());
        Assert.True(store.TryGet("done", out _));
    }

    [Fact]
    public void NonTerminalItem_IsNeverEvicted()
    {
        var now = new DateTime(2026, 07, 16, 12, 0, 0, DateTimeKind.Utc);
        var store = Store(() => now, highWater: 0, retention: TimeSpan.FromMinutes(1));
        var added = store.TryAddAttempt(Item("active"));
        store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Preparing);

        now = now.AddHours(5);

        // Even with the terminal high-water at zero, the non-terminal item is preserved.
        Assert.Single(store.GetSnapshot());
        Assert.True(store.TryGet("active", out _));
    }

    [Fact]
    public void CountHighWater_EvictsOldestTerminalFirst_AndKeepsActive()
    {
        var now = new DateTime(2026, 07, 16, 12, 0, 0, DateTimeKind.Utc);
        var store = Store(() => now, highWater: 3, retention: TimeSpan.FromDays(30));

        // Five terminal items, each completed one minute after the previous.
        for (var index = 0; index < 5; index++)
        {
            var added = store.TryAddAttempt(Item("term-" + index));
            store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Failed);
            now = now.AddMinutes(1);
        }

        // One active item that must survive regardless of the terminal high-water.
        var active = store.TryAddAttempt(Item("active"));
        store.TryTransition(active.Current, HostBridgeDownloadAttemptState.Downloading);

        var snapshot = store.GetSnapshot().Select(item => item.DownloadId).ToHashSet();

        // Oldest two terminal evicted; newest three retained; active always retained.
        Assert.DoesNotContain("term-0", snapshot);
        Assert.DoesNotContain("term-1", snapshot);
        Assert.Contains("term-2", snapshot);
        Assert.Contains("term-3", snapshot);
        Assert.Contains("term-4", snapshot);
        Assert.Contains("active", snapshot);
        Assert.False(store.TryGet("term-0", out _));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
