using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

/// <summary>
/// Crash-recovery matrix for the durable removal coordinator (5B item 1). Each case arms a
/// coordinator fault seam that throws like a killed process at a specific phase, then opens a fresh
/// store and runs recovery, asserting no orphaned files and no double-deletes.
/// </summary>
public sealed class HostBridgeQueueRemovalRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "queue-recover-" + Guid.NewGuid().ToString("N"));

    private string TempFile()
    {
        Directory.CreateDirectory(_root);
        return Path.Combine(_root, "queue.json");
    }

    private string OwnedRoot()
    {
        var root = Path.Combine(_root, "staging");
        Directory.CreateDirectory(root);
        return root;
    }

    private string Child(string name, string evidence)
    {
        var child = Path.Combine(OwnedRoot(), name);
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, evidence), "album evidence");
        return child;
    }

    private static HostBridgeDownloadItem Item(string id, string path) =>
        new() { DownloadId = id, OutputPath = path };

    private HostBridgeDownloadTrackerStore<HostBridgeDownloadItem> Store(
        string persistencePath, string root, IDurableRemovalFaultHooks? faultHooks = null) =>
        new(
            persistencePath: persistencePath,
            options: new HostBridgeQueueStoreOptions
            {
                ContractVersion = HostBridgeQueueContractVersion.AttemptV2,
                OwnedStagingRoot = root,
                RemovalFaultHooks = faultHooks,
            });

    private string JournalDir => Path.Combine(
        OwnedRoot(),
        FileRemovalJournal.RelativeJournalDirectory.Replace('/', Path.DirectorySeparatorChar));

    private string TrashDir => Path.Combine(OwnedRoot(), ".lpc-trash");

    private void AssertWalDrained()
    {
        Assert.True(Directory.Exists(JournalDir));
        Assert.Empty(Directory.EnumerateFiles(JournalDir, "*.json", SearchOption.TopDirectoryOnly));
        Assert.Empty(Directory.EnumerateFiles(JournalDir, "*.compacted", SearchOption.TopDirectoryOnly));
        Assert.Empty(Directory.EnumerateFiles(JournalDir, "*.tmp.*", SearchOption.TopDirectoryOnly));
    }

    private void AssertQuarantineEmpty()
    {
        if (Directory.Exists(TrashDir))
            Assert.Empty(Directory.EnumerateFileSystemEntries(TrashDir));
    }

    public enum CrashPoint
    {
        BeforeQuarantineMove,
        AfterQuarantineMove,
        BeforeMappingRemoval,
        BeforePhysicalDelete,
        AfterPhysicalDelete,
    }

    [Theory]
    [InlineData(CrashPoint.BeforeQuarantineMove)]
    [InlineData(CrashPoint.AfterQuarantineMove)]
    [InlineData(CrashPoint.BeforeMappingRemoval)]
    [InlineData(CrashPoint.BeforePhysicalDelete)]
    [InlineData(CrashPoint.AfterPhysicalDelete)]
    public async Task Crash_AtEveryPhase_RecoversWithoutOrphanOrDoubleDelete(CrashPoint point)
    {
        var persistence = TempFile();
        var output = Child("attempt", "album.flac");
        var crashing = Store(persistence, OwnedRoot(), new CrashHooks(point));
        var added = crashing.TryAddAttempt(Item("attempt", output));
        var failed = crashing.TryTransition(added.Current, HostBridgeDownloadAttemptState.Failed);

        // The fault hook throws like a killed process; the operation does not complete.
        await Assert.ThrowsAsync<InvalidOperationException>(() => crashing.RemoveAttemptAsync(
            failed.Current, deleteData: true, static (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(1)));

        var sourceBeforeRecovery = Directory.Exists(output);
        if (point == CrashPoint.BeforeQuarantineMove)
            Assert.True(sourceBeforeRecovery); // source not yet moved
        else
            Assert.False(sourceBeforeRecovery); // moved into quarantine (and possibly already deleted)

        // A fresh process opens the store and recovers.
        var recovered = Store(persistence, OwnedRoot());
        await recovered.RecoverPendingRemovalsAsync();

        AssertWalDrained();
        AssertQuarantineEmpty();

        if (point == CrashPoint.BeforeQuarantineMove)
        {
            // Abandoned: the destructive phase never began, so the source survives intact.
            Assert.True(Directory.Exists(output));
            Assert.True(File.Exists(Path.Combine(output, "album.flac")));
        }
        else
        {
            // Resumed: the quarantined tree is fully deleted, no orphan remains.
            Assert.False(Directory.Exists(output));
        }

        // Idempotent: a second recovery pass is a clean no-op.
        await recovered.RecoverPendingRemovalsAsync();
        AssertWalDrained();
        AssertQuarantineEmpty();
    }

    [Fact]
    public async Task Crash_AfterQuarantine_FreshStoreCanStillDeleteOtherAttempts()
    {
        // After a crash mid-removal, recovery must release the WAL so subsequent durable removals
        // proceed normally (the single-writer lease is not stranded).
        var persistence = TempFile();
        var first = Child("first", "first.flac");
        var crashing = Store(persistence, OwnedRoot(), new CrashHooks(CrashPoint.BeforePhysicalDelete));
        var addedFirst = crashing.TryAddAttempt(Item("first", first));
        var failedFirst = crashing.TryTransition(addedFirst.Current, HostBridgeDownloadAttemptState.Failed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => crashing.RemoveAttemptAsync(
            failedFirst.Current, true, static (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(1)));

        var recovered = Store(persistence, OwnedRoot());
        await recovered.RecoverPendingRemovalsAsync();
        AssertWalDrained();

        var second = Child("second", "second.flac");
        var addedSecond = recovered.TryAddAttempt(Item("second", second));
        var failedSecond = recovered.TryTransition(addedSecond.Current, HostBridgeDownloadAttemptState.Failed);

        var result = await recovered.RemoveAttemptAsync(
            failedSecond.Current, true, static (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(1));

        Assert.Equal(HostBridgeQueueResultCodes.Removed, result.Code);
        Assert.True(result.FilesRemoved);
        Assert.False(Directory.Exists(second));
        AssertWalDrained();
        AssertQuarantineEmpty();
    }

    [Fact]
    public async Task JournalWriteFault_DuringPrepare_FailsClosedAndLeavesSourceIntact()
    {
        // Crash-fault injection via the journal durability hooks (the 5A seam), routed through the
        // store: a WAL write fault during Prepare fails the removal closed with the source untouched.
        var persistence = TempFile();
        var output = Child("attempt", "album.flac");
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            persistencePath: persistence,
            options: new HostBridgeQueueStoreOptions
            {
                ContractVersion = HostBridgeQueueContractVersion.AttemptV2,
                OwnedStagingRoot = OwnedRoot(),
                RemovalJournalHooks = new OneShotAtomicReplaceFault(),
            });
        var added = store.TryAddAttempt(Item("attempt", output));
        var failed = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Failed);

        var result = await store.RemoveAttemptAsync(
            failed.Current, true, static (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(1));

        Assert.Equal(HostBridgeQueueResultCodes.RemovalJournalFailure, result.Code);
        Assert.False(result.StateRemoved);
        Assert.False(result.FilesRemoved);
        Assert.True(Directory.Exists(output));
        Assert.True(File.Exists(Path.Combine(output, "album.flac")));

        var recovered = Store(persistence, OwnedRoot());
        await recovered.RecoverPendingRemovalsAsync();
        AssertWalDrained();
        Assert.True(Directory.Exists(output));
    }

    [Fact]
    public async Task Recover_OnCleanJournal_IsNoOp()
    {
        var recovered = Store(TempFile(), OwnedRoot());
        await recovered.RecoverPendingRemovalsAsync();
        // No journal directory need exist yet; recovery just returns.
        Assert.True(true);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class CrashHooks : IDurableRemovalFaultHooks
    {
        private readonly CrashPoint _point;

        public CrashHooks(CrashPoint point) => _point = point;

        public void BeforeQuarantineMove() => Throw(CrashPoint.BeforeQuarantineMove);
        public void AfterQuarantineMove() => Throw(CrashPoint.AfterQuarantineMove);
        public void BeforeMappingRemoval() => Throw(CrashPoint.BeforeMappingRemoval);
        public void BeforePhysicalDelete() => Throw(CrashPoint.BeforePhysicalDelete);
        public void AfterPhysicalDelete() => Throw(CrashPoint.AfterPhysicalDelete);

        private void Throw(CrashPoint point)
        {
            if (point == _point)
                throw new InvalidOperationException("simulated crash at " + point);
        }
    }

    private sealed class OneShotAtomicReplaceFault : IRemovalJournalDurabilityHooks
    {
        private bool _fired;

        public void BeforeTempFlush(string path) { }
        public void AfterTempFlush(string path) { }

        public void BeforeAtomicReplace(string temp, string destination)
        {
            if (_fired) return;
            _fired = true;
            throw new IOException("injected WAL durability fault");
        }

        public void AfterAtomicReplace(string destination) { }
        public void BeforeParentFlush(string directory) { }
        public void BeforeCompactionUnlink(string path) { }
        public void BeforeCompactionParentFlush(string directory) { }
    }
}
