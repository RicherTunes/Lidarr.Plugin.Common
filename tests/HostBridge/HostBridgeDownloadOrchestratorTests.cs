using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.HostBridge;

/// <summary>
/// TDD pins for <see cref="HostBridgeDownloadOrchestrator"/>.
/// Covers the five contracts from the Wave A item 2 design:
///
/// 1. Returns a non-empty GUID-shaped string.
/// 2. doWork sees the SNAPSHOT of settings, not the mutated live object
///    (the "ProbeOnly read twice" race fix from PR #130 finding #9).
/// 3. The tracker contains the new item BEFORE the method returns.
/// 4. The method returns without waiting for doWork (fire-and-forget).
/// 5. The CancellationToken flows into doWork.
/// </summary>
public class HostBridgeDownloadOrchestratorTests
{
    // ---------------------------------------------------------------------------
    // Minimal settings stand-in (no Lidarr host refs needed in Common tests)
    // ---------------------------------------------------------------------------
    private sealed class TestSettings
    {
        public bool ProbeOnly { get; set; }
        public string DownloadPath { get; set; } = "/downloads";
        public int Concurrency { get; set; } = 2;

        public TestSettings Clone() => new TestSettings
        {
            ProbeOnly = ProbeOnly,
            DownloadPath = DownloadPath,
            Concurrency = Concurrency
        };
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------
    private static HostBridgeDownloadOrchestrator MakeOrchestrator() =>
        new HostBridgeDownloadOrchestrator(logger: null);

    private static HostBridgeDownloadTrackerStore<HostBridgeDownloadItem> MakeTracker() =>
        new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>();

    private static Func<TestSettings, TestSettings> Snapshotter() =>
        s => s.Clone();

    private static Func<TestSettings, string, HostBridgeDownloadItem> ItemFactory() =>
        (s, id) => new HostBridgeDownloadItem
        {
            DownloadId = id,
            AlbumId = "album-1",
            Title = "Test Album",
            Artist = "Test Artist",
            OutputPath = s.DownloadPath,
            StartedAt = DateTime.UtcNow
        };

    private static async Task<HostBridgeDownloadItem> WaitForPersistedItemAsync(
        string path,
        string downloadId,
        Func<HostBridgeDownloadItem, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var reloaded = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
                persistencePath: path);
            if (reloaded.TryGet(downloadId, out var loaded) && loaded is not null && predicate(loaded))
            {
                return loaded;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Persisted tracker state did not match the expected predicate.");
    }

    // ---------------------------------------------------------------------------
    // Test 1: Returns a GUID-shaped (32 hex chars, no hyphens) string
    // ---------------------------------------------------------------------------
    [Fact]
    public async Task StartTrackedDownloadAsync_ReturnsGuid()
    {
        var orchestrator = MakeOrchestrator();
        var tracker = MakeTracker();
        var settings = new TestSettings();

        string downloadId = await orchestrator.StartTrackedDownloadAsync(
            settings,
            tracker,
            Snapshotter(),
            ItemFactory(),
            doWork: (_, __, ___, ____) => Task.CompletedTask);

        Assert.NotEmpty(downloadId);
        // "N" format = 32 lowercase hex chars, no hyphens
        Assert.Equal(32, downloadId.Length);
        Assert.Matches("^[0-9a-f]{32}$", downloadId);
    }

    // ---------------------------------------------------------------------------
    // Test 2: doWork sees the snapshot, not the mutated live settings
    //         (ProbeOnly race fix)
    // ---------------------------------------------------------------------------
    [Fact]
    public async Task StartTrackedDownloadAsync_SnapshotsSettings_DoWorkSeesSnapshot()
    {
        var orchestrator = MakeOrchestrator();
        var tracker = MakeTracker();
        var settings = new TestSettings { ProbeOnly = false, DownloadPath = "/original" };

        var doWorkSawProbeOnly = new TaskCompletionSource<bool>();
        var doWorkSawPath = new TaskCompletionSource<string?>();
        var doWorkCanStart = new SemaphoreSlim(0, 1);

        string downloadId = await orchestrator.StartTrackedDownloadAsync(
            settings,
            tracker,
            Snapshotter(),
            ItemFactory(),
            doWork: async (snap, _, __, ct) =>
            {
                // Wait until the test mutates the live settings, then capture what snap sees.
                await doWorkCanStart.WaitAsync(ct);
                doWorkSawProbeOnly.TrySetResult(snap.ProbeOnly);
                doWorkSawPath.TrySetResult(snap.DownloadPath);
            });

        // Mutate live settings AFTER Start returns (simulates settings change mid-download)
        settings.ProbeOnly = true;
        settings.DownloadPath = "/changed";

        // Unblock doWork
        doWorkCanStart.Release();

        bool probeOnlyInDoWork = await doWorkSawProbeOnly.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string? pathInDoWork = await doWorkSawPath.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // doWork must see the PRE-MUTATION snapshot values
        Assert.False(probeOnlyInDoWork, "doWork should see ProbeOnly=false (snapshot), not true (mutation)");
        Assert.Equal("/original", pathInDoWork);
    }

    [Fact]
    public async Task StartTrackedDownloadAsync_DefaultSnapshotter_SnapshotsBeforeTaskRun()
    {
        var orchestrator = MakeOrchestrator();
        var tracker = MakeTracker();
        var settings = new TestSettings
        {
            ProbeOnly = false,
            DownloadPath = "/original",
            Concurrency = 2
        };

        var doWorkSawProbeOnly = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var doWorkSawPath = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var doWorkSawConcurrency = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var doWorkCanReadSnapshot = new SemaphoreSlim(0, 1);

        _ = await orchestrator.StartTrackedDownloadAsync(
            settings,
            tracker,
            ItemFactory(),
            doWork: async (snap, _, __, ct) =>
            {
                await doWorkCanReadSnapshot.WaitAsync(ct);
                doWorkSawProbeOnly.TrySetResult(snap.ProbeOnly);
                doWorkSawPath.TrySetResult(snap.DownloadPath);
                doWorkSawConcurrency.TrySetResult(snap.Concurrency);
            });

        settings.ProbeOnly = true;
        settings.DownloadPath = "/changed";
        settings.Concurrency = 8;

        doWorkCanReadSnapshot.Release();

        bool probeOnlyInDoWork = await doWorkSawProbeOnly.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string? pathInDoWork = await doWorkSawPath.Task.WaitAsync(TimeSpan.FromSeconds(5));
        int concurrencyInDoWork = await doWorkSawConcurrency.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(probeOnlyInDoWork, "doWork should see ProbeOnly=false from the Common snapshot, not the mutated live settings.");
        Assert.Equal("/original", pathInDoWork);
        Assert.Equal(2, concurrencyInDoWork);
    }

    // ---------------------------------------------------------------------------
    // Test 3: Tracker contains the item BEFORE the method returns
    // ---------------------------------------------------------------------------
    [Fact]
    public async Task StartTrackedDownloadAsync_AddsItemToTracker_BeforeReturning()
    {
        var orchestrator = MakeOrchestrator();
        var tracker = MakeTracker();
        var settings = new TestSettings();

        // doWork blocks forever until cancelled — ensures the item is in the tracker
        // before doWork has a chance to modify it.
        using var cts = new CancellationTokenSource();

        string downloadId = await orchestrator.StartTrackedDownloadAsync(
            settings,
            tracker,
            Snapshotter(),
            ItemFactory(),
            doWork: (_, __, ___, ct) => Task.Delay(Timeout.Infinite, ct),
            cancellationToken: cts.Token);

        // Cancel to clean up the background task (suppress TaskCanceledException)
        cts.Cancel();
        await Task.Delay(50); // brief yield to let fire-and-forget observe cancellation

        // The tracker must already have the item — no await needed, it was inserted before Task.Run
        Assert.True(tracker.TryGet(downloadId, out var item));
        Assert.NotNull(item);
        Assert.Equal(downloadId, item!.DownloadId);
    }

    // ---------------------------------------------------------------------------
    // Test 4: Method returns without waiting for doWork (fire-and-forget)
    // ---------------------------------------------------------------------------
    [Fact]
    public async Task StartTrackedDownloadAsync_FireAndForget_ReturnsWithoutWaitingForDoWork()
    {
        var orchestrator = MakeOrchestrator();
        var tracker = MakeTracker();
        var settings = new TestSettings();

        var doWorkStarted = new TaskCompletionSource<bool>();
        var doWorkCanFinish = new SemaphoreSlim(0, 1);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // doWork sleeps until explicitly released
        string downloadId = await orchestrator.StartTrackedDownloadAsync(
            settings,
            tracker,
            Snapshotter(),
            ItemFactory(),
            doWork: async (_, __, ___, ct) =>
            {
                doWorkStarted.TrySetResult(true);
                await doWorkCanFinish.WaitAsync(ct);
            });

        sw.Stop();

        // StartTrackedDownloadAsync must have returned almost immediately
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"StartTrackedDownloadAsync took {sw.ElapsedMilliseconds}ms — should be near-instant (fire-and-forget)");

        // Verify the returned value is a valid download ID (not awaited to completion)
        Assert.NotEmpty(downloadId);

        // Clean up: release the background task
        doWorkCanFinish.Release();
        // Let it drain (not required for correctness, just avoids test teardown warnings)
        await Task.Delay(50);
    }

    [Fact]
    public async Task StartTrackedDownloadAsync_PersistsFinalItemStateAfterDoWorkMutation()
    {
        var path = Path.Combine(Path.GetTempPath(), "hbd-orchestrator-" + Guid.NewGuid().ToString("N"), "tracker.json");
        try
        {
            var orchestrator = MakeOrchestrator();
            var tracker = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
                persistencePath: path);
            var settings = new TestSettings();
            var doWorkFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var downloadId = await orchestrator.StartTrackedDownloadAsync(
                settings,
                tracker,
                Snapshotter(),
                ItemFactory(),
                doWork: (_, __, item, ____) =>
                {
                    item.SetStatus(HostBridgeDownloadItemStatus.Completed);
                    item.SetProgress(100);
                    item.CompletedAt = DateTime.UtcNow;
                    doWorkFinished.TrySetResult(true);
                    return Task.CompletedTask;
                });

            await doWorkFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var loaded = await WaitForPersistedItemAsync(
                path,
                downloadId,
                item => item.GetStatus() == HostBridgeDownloadItemStatus.Completed &&
                        Math.Abs(item.GetProgress() - 100) < 0.0001 &&
                        item.CompletedAt.HasValue);

            Assert.Equal(downloadId, loaded.DownloadId);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Test 5: CancellationToken flows into doWork
    // ---------------------------------------------------------------------------
    [Fact]
    public async Task StartTrackedDownloadAsync_CancellationFlows_DoWorkCancellable()
    {
        var orchestrator = MakeOrchestrator();
        var tracker = MakeTracker();
        var settings = new TestSettings();
        using var cts = new CancellationTokenSource();

        var doWorkReceivedToken = new TaskCompletionSource<bool>();
        var doWorkCancelled = new TaskCompletionSource<bool>();

        _ = await orchestrator.StartTrackedDownloadAsync(
            settings,
            tracker,
            Snapshotter(),
            ItemFactory(),
            doWork: async (_, __, ___, ct) =>
            {
                doWorkReceivedToken.TrySetResult(!ct.IsCancellationRequested);
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    doWorkCancelled.TrySetResult(true);
                }
            },
            cancellationToken: cts.Token);

        // Wait until doWork is running
        bool tokenWasNotCancelledOnEntry = await doWorkReceivedToken.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(tokenWasNotCancelledOnEntry, "Token should not be cancelled when doWork starts");

        // Cancel
        cts.Cancel();

        bool doWorkSawCancellation = await doWorkCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(doWorkSawCancellation, "doWork should observe OperationCanceledException when token is cancelled");
    }

    [Fact]
    public async Task StartTrackedDownloadAsync_CancellationOptions_LinksCallerCancellation()
    {
        var orchestrator = MakeOrchestrator();
        var tracker = MakeTracker();
        var settings = new TestSettings();
        using var callerCts = new CancellationTokenSource();
        using var downloadCts = new CancellationTokenSource();

        var doWorkStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var doWorkCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var options = new HostBridgeDownloadStartOptions<HostBridgeDownloadItem>
        {
            RegisterCancellation = (_, __) =>
                new HostBridgeDownloadCancellationRegistration(downloadCts.Token)
        };

        _ = await orchestrator.StartTrackedDownloadAsync(
            settings,
            tracker,
            Snapshotter(),
            ItemFactory(),
            doWork: async (_, __, ___, ct) =>
            {
                doWorkStarted.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    doWorkCancelled.TrySetResult(true);
                    throw;
                }
            },
            options,
            cancellationToken: callerCts.Token);

        await doWorkStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            callerCts.Cancel();
            bool sawCancellation = await doWorkCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(sawCancellation, "caller cancellation must not be masked by a per-download token factory");
        }
        finally
        {
            downloadCts.Cancel();
        }
    }

    [Fact]
    public async Task StartTrackedDownloadAsync_CancellationOptions_RegisteredTokenCancelsWork()
    {
        var orchestrator = MakeOrchestrator();
        var tracker = MakeTracker();
        var settings = new TestSettings();
        using var downloadCts = new CancellationTokenSource();

        var doWorkStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var doWorkCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var options = new HostBridgeDownloadStartOptions<HostBridgeDownloadItem>
        {
            RegisterCancellation = (_, __) =>
                new HostBridgeDownloadCancellationRegistration(downloadCts.Token)
        };

        _ = await orchestrator.StartTrackedDownloadAsync(
            settings,
            tracker,
            Snapshotter(),
            ItemFactory(),
            doWork: async (_, __, ___, ct) =>
            {
                doWorkStarted.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    doWorkCancelled.TrySetResult(true);
                    throw;
                }
            },
            options);

        await doWorkStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        downloadCts.Cancel();

        bool sawCancellation = await doWorkCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(sawCancellation, "the per-download token must cancel work");
    }

    [Fact]
    public async Task StartTrackedDownloadAsync_CancellationOptions_DisposesRegistrationWhenWorkEnds()
    {
        var orchestrator = MakeOrchestrator();
        var tracker = MakeTracker();
        var settings = new TestSettings();

        var registrationDisposed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new HostBridgeDownloadStartOptions<HostBridgeDownloadItem>
        {
            RegisterCancellation = (_, __) =>
                new HostBridgeDownloadCancellationRegistration(
                    CancellationToken.None,
                    () => registrationDisposed.TrySetResult(true))
        };

        _ = await orchestrator.StartTrackedDownloadAsync(
            settings,
            tracker,
            Snapshotter(),
            ItemFactory(),
            doWork: (_, __, ___, ____) => Task.CompletedTask,
            options);

        bool disposed = await registrationDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(disposed, "Common must dispose the per-download cancellation registration after work exits");
    }

    [Fact]
    public async Task StartTrackedDownloadAsync_CancellationOptions_DisposesRegistrationWhenWorkCancels()
    {
        var orchestrator = MakeOrchestrator();
        var tracker = MakeTracker();
        var settings = new TestSettings();
        using var downloadCts = new CancellationTokenSource();

        var doWorkStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrationDisposed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new HostBridgeDownloadStartOptions<HostBridgeDownloadItem>
        {
            RegisterCancellation = (_, __) =>
                new HostBridgeDownloadCancellationRegistration(
                    downloadCts.Token,
                    () => registrationDisposed.TrySetResult(true))
        };

        _ = await orchestrator.StartTrackedDownloadAsync(
            settings,
            tracker,
            Snapshotter(),
            ItemFactory(),
            doWork: async (_, __, ___, ct) =>
            {
                doWorkStarted.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, ct);
            },
            options);

        await doWorkStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        downloadCts.Cancel();

        bool disposed = await registrationDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(disposed, "Common must dispose the registration after cancellation exits doWork");
    }

    [Fact]
    public async Task StartTrackedDownloadAsync_CancellationOptions_DisposesRegistrationWhenWorkFaults()
    {
        var orchestrator = MakeOrchestrator();
        var tracker = MakeTracker();
        var settings = new TestSettings();

        var registrationDisposed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new HostBridgeDownloadStartOptions<HostBridgeDownloadItem>
        {
            RegisterCancellation = (_, __) =>
                new HostBridgeDownloadCancellationRegistration(
                    CancellationToken.None,
                    () => registrationDisposed.TrySetResult(true))
        };

        _ = await orchestrator.StartTrackedDownloadAsync(
            settings,
            tracker,
            Snapshotter(),
            ItemFactory(),
            doWork: (_, __, ___, ____) => throw new InvalidOperationException("boom"),
            options);

        bool disposed = await registrationDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(disposed, "Common must dispose the registration after faulted work exits");
    }

    [Fact]
    public async Task StartTrackedDownloadAsync_CancellationOptions_RegisterFailureRemovesTrackerItem()
    {
        var orchestrator = MakeOrchestrator();
        var tracker = MakeTracker();
        var settings = new TestSettings();
        var options = new HostBridgeDownloadStartOptions<HostBridgeDownloadItem>
        {
            RegisterCancellation = (_, __) => throw new InvalidOperationException("registration failed")
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.StartTrackedDownloadAsync(
                settings,
                tracker,
                Snapshotter(),
                ItemFactory(),
                doWork: (_, __, ___, ____) => Task.CompletedTask,
                options));

        Assert.Empty(tracker.GetSnapshot());
    }
}
