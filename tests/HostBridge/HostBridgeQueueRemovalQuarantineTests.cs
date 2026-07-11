using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

public sealed class HostBridgeQueueRemovalQuarantineTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "queue-quarantine-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SuccessfulRemoval_MovesIntoOwnedTrashBeforeDeleting()
    {
        var hooks = new HostBridgeOwnedStagingHooks();
        string? observedSource = null;
        string? observedQuarantine = null;
        hooks.BeforeQuarantineMove = (source, quarantine) =>
        {
            observedSource = source;
            observedQuarantine = quarantine;
            Assert.True(Directory.Exists(source));
            Assert.Contains(".lpc-trash", quarantine, StringComparison.Ordinal);
        };
        var (store, key, output) = Terminal("success", hooks);

        var result = await Remove(store, key);

        Assert.True(result.StateRemoved);
        Assert.True(result.FilesRemoved);
        Assert.Equal(output, observedSource);
        Assert.NotNull(observedQuarantine);
        Assert.False(Directory.Exists(observedQuarantine));
    }

    [Fact]
    public async Task QuarantineMoveFailure_LeavesRecordAndOriginalTreeUnchanged()
    {
        var hooks = new HostBridgeOwnedStagingHooks
        {
            MoveDirectory = static (_, _) => throw new IOException("injected move failure"),
        };
        var (store, key, output) = Terminal("move-failure", hooks);
        var evidence = Path.Combine(output, "partial.flac");

        var result = await Remove(store, key);

        Assert.Equal(HostBridgeQueueResultCodes.SafeOrphanDeleteFailed, result.Code);
        Assert.False(result.StateRemoved);
        Assert.True(result.SafeOrphanRetained);
        Assert.True(store.TryGet("move-failure", out var retained));
        Assert.Equal(output, retained!.OutputPath);
        Assert.Equal(key, retained.MutationKey());
        Assert.Equal(HostBridgeDownloadAttemptState.Failed, retained.AttemptState);
        Assert.True(File.Exists(evidence));
        Assert.False(Directory.Exists(Path.Combine(_root, "staging", ".lpc-trash")));
    }

    [Fact]
    public async Task InaccessibleTargetInspection_IsDeleteFailureNotMissingNoOp()
    {
        var hooks = new HostBridgeOwnedStagingHooks();
        var (store, key, output) = Terminal("inaccessible", hooks);
        hooks.BeforeInspectPath = path =>
        {
            if (string.Equals(path, output, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("injected inaccessible target");
        };

        var result = await Remove(store, key);

        Assert.Equal(HostBridgeQueueResultCodes.SafeOrphanDeleteFailed, result.Code);
        Assert.False(result.StateRemoved);
        Assert.True(result.SafeOrphanRetained);
        Assert.True(store.TryGet("inaccessible", out var retained));
        Assert.Equal(output, retained!.OutputPath);
        Assert.True(File.Exists(Path.Combine(output, "partial.flac")));
    }

    [SkippableFact]
    public async Task SwapToLinkBeforeMove_RetargetsRecordAndNeverTouchesOutside()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var outsideEvidence = Path.Combine(outside, "precious.flac");
        File.WriteAllText(outsideEvidence, "keep");
        var savedOriginal = Path.Combine(_root, "saved-original");
        var hooks = new HostBridgeOwnedStagingHooks();
        var (store, key, output) = Terminal("swap", hooks);
        hooks.BeforeQuarantineMove = (source, _) =>
        {
            Directory.Move(source, savedOriginal);
            Skip.If(!TryCreateDirectoryLink(source, outside), "Directory link creation is unavailable on this host.");
        };

        var result = await Remove(store, key);

        Assert.Equal(HostBridgeQueueResultCodes.SafeOrphanLinkTraversal, result.Code);
        Assert.False(result.StateRemoved);
        Assert.True(result.SafeOrphanRetained);
        Assert.True(store.TryGet("swap", out var retained));
        Assert.Contains(".lpc-trash", retained!.OutputPath, StringComparison.Ordinal);
        Assert.NotEqual(output, retained.OutputPath);
        Assert.True(File.Exists(outsideEvidence));
        Assert.True(File.Exists(Path.Combine(savedOriginal, "partial.flac")));
    }

    [Fact]
    public async Task PartialDeleteFailure_PersistsQuarantinePathAndExactRetryCompletes()
    {
        var hooks = new HostBridgeOwnedStagingHooks();
        var deleteCount = 0;
        hooks.BeforeDeleteEntry = _ =>
        {
            deleteCount++;
            if (deleteCount == 2) throw new IOException("injected partial delete");
        };
        var (store, key, output) = Terminal("partial", hooks);
        File.WriteAllText(Path.Combine(output, "second.flac"), "second evidence");

        var first = await Remove(store, key);

        Assert.Equal(HostBridgeQueueResultCodes.SafeOrphanDeleteFailed, first.Code);
        Assert.False(first.StateRemoved);
        Assert.True(first.SafeOrphanRetained);
        Assert.True(store.TryGet("partial", out var retained));
        Assert.Contains(".lpc-trash", retained!.OutputPath, StringComparison.Ordinal);
        Assert.True(Directory.Exists(retained.OutputPath));
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(retained.OutputPath));
        Assert.False(Directory.Exists(output));

        var reloadedStore = Store(hooks);
        Assert.True(reloadedStore.TryGet("partial", out var reloaded));
        Assert.Equal(retained.OutputPath, reloaded!.OutputPath);
        Assert.Equal(retained.MutationKey(), reloaded.MutationKey());

        hooks.BeforeDeleteEntry = null;
        var retryKey = new HostBridgeQueueMutationKey(
            reloaded.DownloadId,
            reloaded.AttemptId,
            reloaded.Revision);
        var retry = await Remove(reloadedStore, retryKey);

        Assert.True(retry.StateRemoved);
        Assert.True(retry.FilesRemoved);
        Assert.False(reloadedStore.TryGet("partial", out _));
        Assert.False(Directory.Exists(reloaded.OutputPath));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(300001)]
    public async Task InvalidShutdownTimeout_IsRejectedBeforeAnyMutation(int milliseconds)
    {
        var output = Child("invalid-timeout-" + milliseconds);
        var store = Store();
        var added = store.TryAddAttempt(Item("invalid-timeout-" + milliseconds, output));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.RemoveAttemptAsync(
            added.Current,
            true,
            static (_, _) => Task.CompletedTask,
            TimeSpan.FromMilliseconds(milliseconds)));

        Assert.True(store.TryGet(added.Current.DownloadId, out var retained));
        Assert.Equal(added.Current, retained!.MutationKey());
        Assert.Equal(HostBridgeDownloadAttemptState.Queued, retained.AttemptState);
        Assert.True(Directory.Exists(output));
    }

    [Fact]
    public async Task SynchronouslyBlockingWorker_IsBoundedAtSchedulerBoundary()
    {
        var output = Child("sync-block");
        var store = Store();
        var key = MoveToDownloading(store, Item("sync-block", output));
        using var release = new ManualResetEventSlim(initialState: false);
        var stopwatch = Stopwatch.StartNew();

        var removal = store.RemoveAttemptAsync(
            key,
            true,
            (_, _) =>
            {
                release.Wait(TimeSpan.FromSeconds(2));
                throw new InvalidOperationException("eventual worker fault");
            },
            TimeSpan.FromMilliseconds(50));
        var callElapsed = stopwatch.Elapsed;
        var result = await removal.WaitAsync(TimeSpan.FromSeconds(1));
        release.Set();

        Assert.True(callElapsed < TimeSpan.FromMilliseconds(250));
        Assert.Equal(HostBridgeQueueResultCodes.WorkerShutdownTimeout, result.Code);
        Assert.True(store.TryGet("sync-block", out var retained));
        Assert.Equal(HostBridgeDownloadAttemptState.Cancelling, retained!.AttemptState);
        Assert.True(Directory.Exists(output));
        await Task.Delay(50);
    }

    private (HostBridgeDownloadTrackerStore<HostBridgeDownloadItem> Store,
        HostBridgeQueueMutationKey Key, string Output) Terminal(
        string id,
        HostBridgeOwnedStagingHooks hooks)
    {
        var output = Child(id);
        File.WriteAllText(Path.Combine(output, "partial.flac"), "evidence");
        var store = Store(hooks);
        var added = store.TryAddAttempt(Item(id, output));
        var failed = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Failed);
        return (store, failed.Current, output);
    }

    private HostBridgeDownloadTrackerStore<HostBridgeDownloadItem> Store(
        HostBridgeOwnedStagingHooks? hooks = null) =>
        new(
            persistencePath: Path.Combine(_root, "queue.json"),
            options: new HostBridgeQueueStoreOptions
            {
                ContractVersion = HostBridgeQueueContractVersion.AttemptV2,
                OwnedStagingRoot = OwnedRoot(),
                OwnedStagingHooks = hooks,
            });

    private string OwnedRoot()
    {
        var root = Path.Combine(_root, "staging");
        Directory.CreateDirectory(root);
        return root;
    }

    private string Child(string name)
    {
        var path = Path.Combine(OwnedRoot(), name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static HostBridgeDownloadItem Item(string id, string output) =>
        new() { DownloadId = id, OutputPath = output };

    private static HostBridgeQueueMutationKey MoveToDownloading(
        HostBridgeDownloadTrackerStore<HostBridgeDownloadItem> store,
        HostBridgeDownloadItem item)
    {
        var added = store.TryAddAttempt(item);
        var preparing = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Preparing);
        return store.TryTransition(preparing.Current, HostBridgeDownloadAttemptState.Downloading).Current;
    }

    private static Task<HostBridgeQueueRemovalResult<HostBridgeDownloadItem>> Remove(
        HostBridgeDownloadTrackerStore<HostBridgeDownloadItem> store,
        HostBridgeQueueMutationKey key) =>
        store.RemoveAttemptAsync(
            key,
            true,
            static (_, _) => Task.CompletedTask,
            TimeSpan.FromSeconds(1));

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

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
