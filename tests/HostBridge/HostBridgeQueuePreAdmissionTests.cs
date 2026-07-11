using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.HostBridge;

public class HostBridgeQueuePreAdmissionTests : IDisposable
{
    private const string EmptyPersistence = "{\"schemaVersion\":2,\"items\":[]}";

    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), "queue-pre-admission-" + Guid.NewGuid().ToString("N"));

    private sealed class Settings
    {
        public string Value { get; set; } = string.Empty;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDirectory, recursive: true); } catch { /* best-effort */ }
    }

    private HostBridgeDownloadTrackerStore<HostBridgeDownloadItem> V2Store(string? persistencePath = null) =>
        new(persistencePath: persistencePath, options: new HostBridgeQueueStoreOptions
        {
            ContractVersion = HostBridgeQueueContractVersion.AttemptV2,
        });

    private (string PersistencePath, string StagingSentinel) CreateUntouchedArtifacts()
    {
        Directory.CreateDirectory(_tempDirectory);
        var persistencePath = Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N") + ".json");
        var stagingDirectory = Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        var stagingSentinel = Path.Combine(stagingDirectory, "sentinel.part");
        File.WriteAllText(persistencePath, EmptyPersistence);
        File.WriteAllText(stagingSentinel, "partial-download");
        return (persistencePath, stagingSentinel);
    }

    private static void AssertArtifactsUntouched(string persistencePath, string stagingSentinel)
    {
        Assert.Equal(EmptyPersistence, File.ReadAllText(persistencePath));
        Assert.Equal("partial-download", File.ReadAllText(stagingSentinel));
    }

    [Fact]
    public async Task RejectedAdmission_DoesNotPersistRegisterOrSchedule()
    {
        var events = new List<string>();
        var artifacts = CreateUntouchedArtifacts();
        var tracker = V2Store(artifacts.PersistencePath);
        var options = new HostBridgeDownloadStartOptions<HostBridgeDownloadItem>
        {
            RegisterCancellation = (_, _) =>
            {
                events.Add("register");
                return new HostBridgeDownloadCancellationRegistration(CancellationToken.None);
            },
        };

        var error = await Assert.ThrowsAsync<HostBridgePreAdmissionException>(() =>
            new HostBridgeDownloadOrchestrator().StartTrackedDownloadV2Async(
                new Settings(), tracker,
                static settings => new Settings { Value = settings.Value },
                (_, id) => { events.Add("item"); return new HostBridgeDownloadItem { DownloadId = id, OutputPath = Path.GetDirectoryName(artifacts.StagingSentinel)! }; },
                (_, _, _) => { events.Add("admission"); return ValueTask.FromResult(HostBridgePreAdmissionResult.Reject("DRM_DEVICE_REQUIRED", "Select a personal device.")); },
                (_, _, _, _) => { events.Add("work"); return Task.CompletedTask; },
                options));

        Assert.Equal("DRM_DEVICE_REQUIRED", error.Code);
        Assert.Equal(new[] { "item", "admission" }, events);
        Assert.Empty(tracker.GetSnapshot());
        AssertArtifactsUntouched(artifacts.PersistencePath, artifacts.StagingSentinel);
    }

    [Fact]
    public async Task AdmissionCallbackFailure_LeavesStorageEmpty()
    {
        var artifacts = CreateUntouchedArtifacts();
        var tracker = V2Store(artifacts.PersistencePath);
        var workCalled = false;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new HostBridgeDownloadOrchestrator().StartTrackedDownloadV2Async(
                new Settings(), tracker,
                static settings => new Settings { Value = settings.Value },
                (_, id) => new HostBridgeDownloadItem { DownloadId = id, OutputPath = Path.GetDirectoryName(artifacts.StagingSentinel)! },
                static async (_, _, _) =>
                {
                    await Task.Yield();
                    throw new InvalidOperationException("admission failed");
                },
                (_, _, _, _) => { workCalled = true; return Task.CompletedTask; },
                new HostBridgeDownloadStartOptions<HostBridgeDownloadItem>()));

        Assert.Equal("admission failed", error.Message);
        Assert.False(workCalled);
        Assert.Empty(tracker.GetSnapshot());
        AssertArtifactsUntouched(artifacts.PersistencePath, artifacts.StagingSentinel);
    }

    [Fact]
    public async Task CallerCancellationAfterAdmission_LeavesStorageEmpty()
    {
        var events = new List<string>();
        var artifacts = CreateUntouchedArtifacts();
        var tracker = V2Store(artifacts.PersistencePath);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HostBridgeDownloadOrchestrator().StartTrackedDownloadV2Async(
                new Settings(), tracker,
                static settings => new Settings { Value = settings.Value },
                (_, id) => { events.Add("item"); return new HostBridgeDownloadItem { DownloadId = id, OutputPath = Path.GetDirectoryName(artifacts.StagingSentinel)! }; },
                (_, _, _) =>
                {
                    events.Add("admission");
                    cancellation.Cancel();
                    return ValueTask.FromResult(HostBridgePreAdmissionResult.Allow());
                },
                (_, _, _, _) => { events.Add("work"); return Task.CompletedTask; },
                new HostBridgeDownloadStartOptions<HostBridgeDownloadItem>
                {
                    RegisterCancellation = (_, _) =>
                    {
                        events.Add("register");
                        return new HostBridgeDownloadCancellationRegistration(CancellationToken.None);
                    },
                },
                cancellation.Token));

        Assert.Equal(new[] { "item", "admission" }, events);
        Assert.Empty(tracker.GetSnapshot());
        AssertArtifactsUntouched(artifacts.PersistencePath, artifacts.StagingSentinel);
    }

    [Fact]
    public async Task AcceptedAdmission_UsesSnapshotAndCommitsBeforeRegistrationAndWork()
    {
        var events = new List<string>();
        var workStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracker = V2Store();
        var settings = new Settings { Value = "snapshot-value" };

        string downloadId = await new HostBridgeDownloadOrchestrator().StartTrackedDownloadV2Async(
            settings, tracker,
            source =>
            {
                var snapshot = new Settings { Value = source.Value };
                source.Value = "mutated-live-value";
                return snapshot;
            },
            (snapshot, id) =>
            {
                Assert.Equal("snapshot-value", snapshot.Value);
                events.Add("item");
                return new HostBridgeDownloadItem { DownloadId = id };
            },
            (snapshot, _, _) =>
            {
                Assert.Equal("snapshot-value", snapshot.Value);
                events.Add("admission");
                return ValueTask.FromResult(HostBridgePreAdmissionResult.Allow());
            },
            (snapshot, id, _, _) =>
            {
                Assert.Equal("snapshot-value", snapshot.Value);
                Assert.True(tracker.TryGet(id, out _));
                events.Add("work");
                workStarted.TrySetResult();
                return Task.CompletedTask;
            },
            new HostBridgeDownloadStartOptions<HostBridgeDownloadItem>
            {
                RegisterCancellation = (id, item) =>
                {
                    Assert.True(tracker.TryGet(id, out _));
                    events.Add("register");
                    return new HostBridgeDownloadCancellationRegistration(CancellationToken.None);
                },
            });

        await workStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("mutated-live-value", settings.Value);
        Assert.True(tracker.TryGet(downloadId, out _));
        Assert.Single(tracker.GetSnapshot());
        Assert.Equal(new[] { "item", "admission", "register", "work" }, events);
    }

    [Fact]
    public async Task RegistrationFailure_RollsBackCommittedV2ItemWithoutSchedulingOrDeletingStaging()
    {
        var artifacts = CreateUntouchedArtifacts();
        var tracker = V2Store(artifacts.PersistencePath);
        var workCalled = false;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new HostBridgeDownloadOrchestrator().StartTrackedDownloadV2Async(
                new Settings(), tracker,
                static settings => new Settings { Value = settings.Value },
                (_, id) => new HostBridgeDownloadItem
                {
                    DownloadId = id,
                    OutputPath = Path.GetDirectoryName(artifacts.StagingSentinel)!,
                },
                static (_, _, _) => ValueTask.FromResult(HostBridgePreAdmissionResult.Allow()),
                (_, _, _, _) => { workCalled = true; return Task.CompletedTask; },
                new HostBridgeDownloadStartOptions<HostBridgeDownloadItem>
                {
                    RegisterCancellation = (_, _) => throw new InvalidOperationException("registration failed"),
                }));

        Assert.Equal("registration failed", error.Message);
        Assert.False(workCalled);
        Assert.Empty(tracker.GetSnapshot());
        Assert.DoesNotContain("downloadId", File.ReadAllText(artifacts.PersistencePath), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("partial-download", File.ReadAllText(artifacts.StagingSentinel));
    }

    [Fact]
    public async Task QueueConflict_DoesNotReplaceExistingAttemptRegisterOrSchedule()
    {
        var tracker = V2Store();
        var existing = new HostBridgeDownloadItem { DownloadId = "collision" };
        Assert.True(tracker.TryAddAttempt(existing).Applied);
        var events = new List<string>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new HostBridgeDownloadOrchestrator().StartTrackedDownloadV2Async(
                new Settings(), tracker,
                static settings => new Settings { Value = settings.Value },
                static (_, _) => new HostBridgeDownloadItem { DownloadId = "COLLISION" },
                static (_, _, _) => ValueTask.FromResult(HostBridgePreAdmissionResult.Allow()),
                (_, _, _, _) => { events.Add("work"); return Task.CompletedTask; },
                new HostBridgeDownloadStartOptions<HostBridgeDownloadItem>
                {
                    RegisterCancellation = (_, _) =>
                    {
                        events.Add("register");
                        return new HostBridgeDownloadCancellationRegistration(CancellationToken.None);
                    },
                }));

        Assert.Equal(HostBridgeQueueResultCodes.Conflict, error.Message);
        Assert.Empty(events);
        Assert.Single(tracker.GetSnapshot());
        Assert.True(tracker.TryGet("collision", out var current));
        Assert.Same(existing, current);
    }

    [Fact]
    public async Task ConcurrentAcceptedAdmissions_AlwaysCommitBeforeRegistrationAndWork()
    {
        const int attemptCount = 64;
        var tracker = V2Store();
        var registered = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var orderingViolations = new ConcurrentQueue<string>();
        var allWorkStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workStartedCount = 0;

        var starts = new Task<string>[attemptCount];
        for (var index = 0; index < starts.Length; index++)
        {
            starts[index] = new HostBridgeDownloadOrchestrator().StartTrackedDownloadV2Async(
                new Settings { Value = index.ToString() }, tracker,
                static settings => new Settings { Value = settings.Value },
                static (_, id) => new HostBridgeDownloadItem { DownloadId = id },
                async (_, id, _) =>
                {
                    await Task.Yield();
                    Assert.False(tracker.TryGet(id, out _));
                    return HostBridgePreAdmissionResult.Allow();
                },
                (_, id, _, _) =>
                {
                    if (!tracker.TryGet(id, out _)) orderingViolations.Enqueue("work-before-commit");
                    if (!registered.ContainsKey(id)) orderingViolations.Enqueue("work-before-register");
                    if (Interlocked.Increment(ref workStartedCount) == attemptCount)
                        allWorkStarted.TrySetResult();
                    return Task.CompletedTask;
                },
                new HostBridgeDownloadStartOptions<HostBridgeDownloadItem>
                {
                    RegisterCancellation = (id, item) =>
                    {
                        Assert.True(tracker.TryGet(id, out _));
                        Assert.True(registered.TryAdd(id, 0));
                        return new HostBridgeDownloadCancellationRegistration(CancellationToken.None);
                    },
                });
        }

        string[] downloadIds = await Task.WhenAll(starts);
        await allWorkStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(attemptCount, downloadIds.Length);
        Assert.Equal(attemptCount, registered.Count);
        Assert.Equal(attemptCount, tracker.GetSnapshot().Count());
        Assert.Empty(orderingViolations);
    }
}
