using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

public sealed class HostBridgeQueueRemovalV2Tests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "queue-remove-" + Guid.NewGuid().ToString("N"));

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

    private string Child(string name)
    {
        var child = Path.Combine(OwnedRoot(), name);
        Directory.CreateDirectory(child);
        return child;
    }

    private static HostBridgeDownloadItem Item(string id, string path) =>
        new() { DownloadId = id, OutputPath = path };

    private static HostBridgeDownloadTrackerStore<HostBridgeDownloadItem> V2(
        string persistencePath,
        string root) =>
        new(
            persistencePath: persistencePath,
            options: new HostBridgeQueueStoreOptions
            {
                ContractVersion = HostBridgeQueueContractVersion.AttemptV2,
                OwnedStagingRoot = root,
            });

    private static HostBridgeQueueMutationKey MoveToDownloading(
        HostBridgeDownloadTrackerStore<HostBridgeDownloadItem> store,
        HostBridgeDownloadItem item)
    {
        var added = store.TryAddAttempt(item);
        var preparing = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Preparing);
        return store.TryTransition(
            preparing.Current,
            HostBridgeDownloadAttemptState.Downloading).Current;
    }

    [Fact]
    public async Task ActiveRemoval_PersistsCancelling_AwaitsWorker_ThenDefersDeletion()
    {
        var path = TempFile();
        var store = V2(path, OwnedRoot());
        var added = store.TryAddAttempt(Item("active", Child("active")));
        var preparing = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Preparing);
        var downloading = store.TryTransition(preparing.Current, HostBridgeDownloadAttemptState.Downloading);
        var releaseWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancelling = false;

        var removal = store.RemoveAttemptAsync(
            downloading.Current, true,
            async (_, token) =>
            {
                observedCancelling = File.ReadAllText(path).Contains("Cancelling", StringComparison.Ordinal);
                await releaseWorker.Task.WaitAsync(token);
            },
            TimeSpan.FromSeconds(2));

        Assert.False(removal.IsCompleted);
        releaseWorker.SetResult();
        var result = await removal;
        Assert.True(observedCancelling);
        Assert.False(result.StateRemoved);
        Assert.False(result.FilesRemoved);
        Assert.True(result.MappingRetained);
        Assert.Equal(HostBridgeQueueResultCodes.RemovalDeferred, result.Code);
        Assert.True(store.TryGet("ACTIVE", out var retained));
        Assert.Equal(HostBridgeDownloadAttemptState.Cancelled, retained!.AttemptState);
    }

    [Fact]
    public async Task ShutdownTimeout_RetainsCancellingRecordAndFiles()
    {
        var store = V2(TempFile(), OwnedRoot());
        var output = Child("timeout");
        File.WriteAllText(Path.Combine(output, "partial.flac"), "orphan evidence");
        var key = MoveToDownloading(store, Item("timeout", output));
        var result = await store.RemoveAttemptAsync(
            key, true,
            (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
            TimeSpan.FromMilliseconds(50));

        Assert.Equal(HostBridgeQueueResultCodes.WorkerShutdownTimeout, result.Code);
        Assert.False(result.StateRemoved);
        Assert.False(result.FilesRemoved);
        Assert.True(store.TryGet("timeout", out var retained));
        Assert.Equal(HostBridgeDownloadAttemptState.Cancelling, retained!.AttemptState);
        Assert.True(Directory.Exists(output));
        Assert.True(File.Exists(Path.Combine(output, "partial.flac")));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task ShutdownTimeout_BoundsWorkerThatIgnoresCancellation()
    {
        var output = Child("ignores-cancellation");
        File.WriteAllText(Path.Combine(output, "partial.flac"), "orphan evidence");
        var store = V2(TempFile(), OwnedRoot());
        var key = MoveToDownloading(store, Item("ignores-cancellation", output));
        var workerNeverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await store.RemoveAttemptAsync(
                key, true,
                (_, _) => workerNeverCompletes.Task,
                TimeSpan.FromMilliseconds(30))
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(HostBridgeQueueResultCodes.WorkerShutdownTimeout, result.Code);
        Assert.False(result.StateRemoved);
        Assert.False(result.FilesRemoved);
        Assert.True(store.TryGet("ignores-cancellation", out var retained));
        Assert.Equal(HostBridgeDownloadAttemptState.Cancelling, retained!.AttemptState);
        Assert.True(File.Exists(Path.Combine(output, "partial.flac")));
    }

    [Fact]
    public async Task WorkerFailure_PropagatesAndRetainsCancellingRecordAndFiles()
    {
        var output = Child("worker-failure");
        var store = V2(TempFile(), OwnedRoot());
        var key = MoveToDownloading(store, Item("worker-failure", output));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.RemoveAttemptAsync(
            key, true,
            static (_, _) => throw new InvalidOperationException("worker failed"),
            TimeSpan.FromSeconds(1)));

        Assert.Equal("worker failed", error.Message);
        Assert.True(store.TryGet("worker-failure", out var retained));
        Assert.Equal(HostBridgeDownloadAttemptState.Cancelling, retained!.AttemptState);
        Assert.True(Directory.Exists(output));
    }

    [Fact]
    public async Task WorkerIndependentCancellation_PropagatesAndRetainsCancellingRecordAndFiles()
    {
        var output = Child("worker-cancel");
        var store = V2(TempFile(), OwnedRoot());
        var key = MoveToDownloading(store, Item("worker-cancel", output));

        await Assert.ThrowsAsync<TaskCanceledException>(() => store.RemoveAttemptAsync(
            key, true,
            static (_, _) => Task.FromCanceled(new CancellationToken(canceled: true)),
            TimeSpan.FromSeconds(1)));

        Assert.True(store.TryGet("worker-cancel", out var retained));
        Assert.Equal(HostBridgeDownloadAttemptState.Cancelling, retained!.AttemptState);
        Assert.True(Directory.Exists(output));
    }

    [Theory(Skip = "Superseded by 5A deferred removal until the durable coordinator lands.")]
    [InlineData("root")]
    [InlineData("sibling")]
    [InlineData("dotdot")]
    public async Task OutsideOrNonDescendantTarget_IsRetainedWithStableCode(string targetKind)
    {
        var root = OwnedRoot();
        var target = targetKind switch
        {
            "root" => root,
            "sibling" => root + "-sibling",
            _ => Path.Combine(root, "..", "escape"),
        };
        Directory.CreateDirectory(Path.GetFullPath(target));
        var store = V2(TempFile(), root);
        var added = store.TryAddAttempt(Item(targetKind, target));
        var failed = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Failed);

        var result = await store.RemoveAttemptAsync(
            failed.Current, true, static (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(1));

        Assert.Equal(HostBridgeQueueResultCodes.SafeOrphanOutsideRoot, result.Code);
        Assert.False(result.StateRemoved);
        Assert.False(result.FilesRemoved);
        Assert.True(result.SafeOrphanRetained);
        Assert.True(store.TryGet(targetKind, out _));
        Assert.True(Directory.Exists(Path.GetFullPath(target)));
    }

    [Fact(Skip = "Superseded by 5A deferred removal until the durable coordinator lands.")]
    public async Task MissingInRootTarget_IsSuccessfulDeleteNoOp()
    {
        var root = OwnedRoot();
        var missing = Path.Combine(root, "missing");
        var store = V2(TempFile(), root);
        var added = store.TryAddAttempt(Item("missing", missing));
        var failed = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Failed);

        var result = await store.RemoveAttemptAsync(
            failed.Current, true, static (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(1));

        Assert.Equal(HostBridgeQueueResultCodes.Removed, result.Code);
        Assert.True(result.StateRemoved);
        Assert.True(result.FilesRemoved);
        Assert.False(result.SafeOrphanRetained);
    }

    [Fact(Skip = "Superseded by 5A deferred removal until the durable coordinator lands.")]
    public async Task AnotherActiveAttemptOwningCanonicalPath_PreservesFilesButRemovesTerminalRecord()
    {
        var output = Child("shared");
        File.WriteAllText(Path.Combine(output, "partial.flac"), "evidence");
        var store = V2(TempFile(), OwnedRoot());
        var old = store.TryAddAttempt(Item("old", output));
        var failed = store.TryTransition(old.Current, HostBridgeDownloadAttemptState.Failed);
        _ = MoveToDownloading(store, Item("new", Path.Combine(output, ".")));

        var result = await store.RemoveAttemptAsync(
            failed.Current, true, static (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(1));

        Assert.Equal(HostBridgeQueueResultCodes.Removed, result.Code);
        Assert.True(result.StateRemoved);
        Assert.False(result.FilesRemoved);
        Assert.False(result.SafeOrphanRetained);
        Assert.False(store.TryGet("old", out _));
        Assert.True(store.TryGet("new", out _));
        Assert.True(Directory.Exists(output));
    }

    [Fact(Skip = "Superseded by 5A deferred removal until the durable coordinator lands.")]
    public async Task TerminalAttempt_SkipsWorkerAndRemovesDirectly()
    {
        var output = Child("terminal");
        var store = V2(TempFile(), OwnedRoot());
        var added = store.TryAddAttempt(Item("terminal", output));
        var failed = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Failed);
        var workerCalled = false;

        var result = await store.RemoveAttemptAsync(
            failed.Current, true,
            (_, _) =>
            {
                workerCalled = true;
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(1));

        Assert.False(workerCalled);
        Assert.True(result.StateRemoved);
        Assert.True(result.FilesRemoved);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task StaleCas_DoesNotStopWorkerDeleteFilesOrRemoveState()
    {
        var output = Child("stale");
        var store = V2(TempFile(), OwnedRoot());
        var added = store.TryAddAttempt(Item("stale", output));
        var preparing = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Preparing);
        var workerCalled = false;

        var result = await store.RemoveAttemptAsync(
            added.Current, true,
            (_, _) =>
            {
                workerCalled = true;
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(1));

        Assert.Equal(HostBridgeQueueResultCodes.Conflict, result.Code);
        Assert.False(workerCalled);
        Assert.False(result.StateRemoved);
        Assert.True(store.TryGet("stale", out var retained));
        Assert.Equal(preparing.Current, retained!.MutationKey());
        Assert.True(Directory.Exists(output));
    }

    [Fact(Skip = "Superseded by 5A deferred removal until the durable coordinator lands.")]
    public async Task TargetReparsePoint_IsRetainedAndOutsideTargetSurvives()
    {
        var root = OwnedRoot();
        var outside = Path.Combine(_root, "outside-target");
        Directory.CreateDirectory(outside);
        var evidence = Path.Combine(outside, "precious.flac");
        File.WriteAllText(evidence, "keep");
        var link = Path.Combine(root, "linked-target");
        if (!TryCreateDirectoryLink(link, outside)) return;
        var store = V2(TempFile(), root);
        var added = store.TryAddAttempt(Item("link", link));
        var failed = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Failed);

        var result = await store.RemoveAttemptAsync(
            failed.Current, true, static (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(1));

        Assert.Equal(HostBridgeQueueResultCodes.SafeOrphanLinkTraversal, result.Code);
        Assert.True(result.SafeOrphanRetained);
        Assert.False(result.StateRemoved);
        Assert.True(File.Exists(evidence));
    }

    [Fact(Skip = "Superseded by 5A deferred removal until the durable coordinator lands.")]
    public async Task RootReparsePoint_IsRetainedAndOutsideTargetSurvives()
    {
        Directory.CreateDirectory(_root);
        var outside = Path.Combine(_root, "physical-root");
        Directory.CreateDirectory(outside);
        var linkRoot = Path.Combine(_root, "linked-root");
        if (!TryCreateDirectoryLink(linkRoot, outside)) return;
        var target = Path.Combine(linkRoot, "attempt");
        Directory.CreateDirectory(target);
        var evidence = Path.Combine(target, "precious.flac");
        File.WriteAllText(evidence, "keep");
        var store = V2(TempFile(), linkRoot);
        var added = store.TryAddAttempt(Item("root-link", target));
        var failed = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Failed);

        var result = await store.RemoveAttemptAsync(
            failed.Current, true, static (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(1));

        Assert.Equal(HostBridgeQueueResultCodes.SafeOrphanLinkTraversal, result.Code);
        Assert.True(result.SafeOrphanRetained);
        Assert.False(result.StateRemoved);
        Assert.True(File.Exists(evidence));
    }

    [Fact(Skip = "Superseded by 5A deferred removal until the durable coordinator lands.")]
    public async Task NestedReparsePoint_IsRetainedAndOutsideTargetSurvives()
    {
        var target = Child("nested-link");
        var localEvidence = Path.Combine(target, "partial.flac");
        File.WriteAllText(localEvidence, "retain before refusal");
        var outside = Path.Combine(_root, "nested-outside");
        Directory.CreateDirectory(outside);
        var evidence = Path.Combine(outside, "precious.flac");
        File.WriteAllText(evidence, "keep");
        if (!TryCreateDirectoryLink(Path.Combine(target, "escape"), outside)) return;
        var store = V2(TempFile(), OwnedRoot());
        var added = store.TryAddAttempt(Item("nested-link", target));
        var failed = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Failed);

        var result = await store.RemoveAttemptAsync(
            failed.Current, true, static (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(1));

        Assert.Equal(HostBridgeQueueResultCodes.SafeOrphanLinkTraversal, result.Code);
        Assert.True(result.SafeOrphanRetained);
        Assert.False(result.StateRemoved);
        Assert.True(Directory.Exists(target));
        Assert.True(File.Exists(localEvidence));
        Assert.True(File.Exists(evidence));
    }

    [Fact(Skip = "Superseded by 5A deferred removal until the durable coordinator lands.")]
    public async Task DanglingTargetSymlink_IsRetainedOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = OwnedRoot();
        var link = Path.Combine(root, "dangling-target");
        if (!TryCreateDanglingDirectoryLink(link, Path.Combine(_root, "missing-target"))) return;
        var store = V2(TempFile(), root);
        var added = store.TryAddAttempt(Item("dangling-target", link));
        var failed = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Failed);

        var result = await store.RemoveAttemptAsync(
            failed.Current, true, static (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(1));

        Assert.Equal(HostBridgeQueueResultCodes.SafeOrphanLinkTraversal, result.Code);
        Assert.True(result.SafeOrphanRetained);
        Assert.False(result.StateRemoved);
        Assert.True(store.TryGet("dangling-target", out _));
        Assert.True(IsReparsePoint(link));
    }

    [Fact(Skip = "Superseded by 5A deferred removal until the durable coordinator lands.")]
    public async Task DanglingRootSymlink_IsRetainedOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(_root);
        var linkRoot = Path.Combine(_root, "dangling-root");
        if (!TryCreateDanglingDirectoryLink(linkRoot, Path.Combine(_root, "missing-root"))) return;
        var target = Path.Combine(linkRoot, "attempt");
        var store = V2(TempFile(), linkRoot);
        var added = store.TryAddAttempt(Item("dangling-root", target));
        var failed = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Failed);

        var result = await store.RemoveAttemptAsync(
            failed.Current, true, static (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(1));

        Assert.Equal(HostBridgeQueueResultCodes.SafeOrphanLinkTraversal, result.Code);
        Assert.True(result.SafeOrphanRetained);
        Assert.False(result.StateRemoved);
        Assert.True(store.TryGet("dangling-root", out _));
        Assert.True(IsReparsePoint(linkRoot));
    }

    [Fact(Skip = "Superseded by 5A deferred removal until the durable coordinator lands.")]
    public async Task RootFilesystemIdentityIsRevalidatedAtRemovalTime()
    {
        Directory.CreateDirectory(_root);
        var originalRoot = Path.Combine(_root, "replaceable-root");
        Directory.CreateDirectory(originalRoot);
        var target = Path.Combine(originalRoot, "attempt");
        Directory.CreateDirectory(target);
        var store = V2(TempFile(), originalRoot);
        var added = store.TryAddAttempt(Item("root-swap", target));
        var failed = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Failed);

        Directory.Delete(originalRoot, recursive: true);
        var outside = Path.Combine(_root, "replacement-target");
        Directory.CreateDirectory(Path.Combine(outside, "attempt"));
        var evidence = Path.Combine(outside, "attempt", "precious.flac");
        File.WriteAllText(evidence, "keep");
        if (!TryCreateDirectoryLink(originalRoot, outside)) return;

        var result = await store.RemoveAttemptAsync(
            failed.Current, true, static (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(1));

        Assert.Equal(HostBridgeQueueResultCodes.SafeOrphanLinkTraversal, result.Code);
        Assert.False(result.StateRemoved);
        Assert.True(File.Exists(evidence));
    }

    [Fact(Skip = "Superseded by 5A deferred removal until the durable coordinator lands.")]
    public async Task ConfiguredOwnedRoot_IsSnapshottedWhenStoreIsConstructed()
    {
        var originalRoot = OwnedRoot();
        var output = Child("immutable-root");
        var attackerRoot = Path.Combine(_root, "attacker-root");
        Directory.CreateDirectory(attackerRoot);
        var options = new HostBridgeQueueStoreOptions
        {
            ContractVersion = HostBridgeQueueContractVersion.AttemptV2,
            OwnedStagingRoot = originalRoot,
        };
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            persistencePath: TempFile(),
            options: options);
        var added = store.TryAddAttempt(Item("immutable-root", output));
        var failed = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Failed);
        typeof(HostBridgeQueueStoreOptions)
            .GetProperty(nameof(HostBridgeQueueStoreOptions.OwnedStagingRoot))!
            .SetValue(options, attackerRoot);

        var result = await store.RemoveAttemptAsync(
            failed.Current, true, static (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(1));

        Assert.Equal(HostBridgeQueueResultCodes.Removed, result.Code);
        Assert.True(result.FilesRemoved);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task WarningObserverFailure_DoesNotBreakCommittedRemoval()
    {
        var persistenceDirectory = Path.Combine(_root, "persistence-is-directory");
        Directory.CreateDirectory(persistenceDirectory);
        var throwWarnings = false;
        var throwingObserverCalls = 0;
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            persistencePath: persistenceDirectory,
            onWarn: _ =>
            {
                if (throwWarnings)
                {
                    throwingObserverCalls++;
                    throw new InvalidOperationException("observer failed");
                }
            },
            options: new HostBridgeQueueStoreOptions
            {
                ContractVersion = HostBridgeQueueContractVersion.AttemptV2,
                OwnedStagingRoot = OwnedRoot(),
            });
        var added = store.TryAddAttempt(Item("observer", Child("observer")));
        var failed = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Failed);
        throwWarnings = true;

        var result = await store.RemoveAttemptAsync(
            failed.Current, false, static (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(1));

        Assert.True(result.StateRemoved);
        Assert.Equal(1, throwingObserverCalls);
        Assert.False(store.TryGet("observer", out _));
    }

    [Fact]
    public void LegacyRemove_RemainsUnchanged()
    {
        var output = Child("legacy");
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>();
        store.AddOrReplace(Item(" legacy-id ", output));

        Assert.False(store.Remove("legacy-id", deleteData: true, out _));
        Assert.True(store.Remove(" legacy-id ", deleteData: true, out var removed));
        Assert.Equal(" legacy-id ", removed!.DownloadId);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void LegacyWarningObserverFailure_StillPropagates()
    {
        var persistenceDirectory = Path.Combine(_root, "legacy-persistence-is-directory");
        Directory.CreateDirectory(persistenceDirectory);
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            persistencePath: persistenceDirectory,
            onWarn: _ => throw new InvalidOperationException("legacy observer failed"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            store.AddOrReplace(Item("legacy-observer", Child("legacy-observer"))));

        Assert.Equal("legacy observer failed", error.Message);
        Assert.True(store.TryGet("legacy-observer", out _));
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var startInfo = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(startInfo);
                process?.WaitForExit(5000);
            }
            catch
            {
                return false;
            }

            return Directory.Exists(link)
                && (new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0;
        }

        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateDanglingDirectoryLink(string link, string missingTarget)
    {
        try
        {
            Directory.CreateSymbolicLink(link, missingTarget);
            return IsReparsePoint(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return false; }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
