using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.HostBridge;
using Microsoft.Extensions.Logging;
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

    private sealed class ThrowingLogger : ILogger
    {
        private sealed class Scope : IDisposable
        {
            public void Dispose() { }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Scope();
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger failed");
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
        Directory.CreateDirectory(_tempDirectory);
        var persistencePath = Path.Combine(_tempDirectory, "accepted-ordering.json");
        var tracker = V2Store(persistencePath);
        var settings = new Settings { Value = "snapshot-value" };

        void AssertPersisted(string id)
        {
            Assert.True(File.Exists(persistencePath));
            Assert.Contains(id, File.ReadAllText(persistencePath), StringComparison.OrdinalIgnoreCase);
        }

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
                AssertPersisted(id);
                events.Add("work");
                workStarted.TrySetResult();
                return Task.CompletedTask;
            },
            new HostBridgeDownloadStartOptions<HostBridgeDownloadItem>
            {
                RegisterCancellation = (id, item) =>
                {
                    Assert.True(tracker.TryGet(id, out _));
                    AssertPersisted(id);
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
    public async Task RegistrationFailure_DoesNotRollbackReentrantReplacementAttempt()
    {
        Directory.CreateDirectory(_tempDirectory);
        var persistencePath = Path.Combine(_tempDirectory, "replacement-rollback.json");
        var tracker = V2Store(persistencePath);
        HostBridgeDownloadItem? replacement = null;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new HostBridgeDownloadOrchestrator().StartTrackedDownloadV2Async(
                new Settings(), tracker,
                static settings => new Settings { Value = settings.Value },
                static (_, id) => new HostBridgeDownloadItem { DownloadId = id, Title = "committed" },
                static (_, _, _) => ValueTask.FromResult(HostBridgePreAdmissionResult.Allow()),
                static (_, _, _, _) => Task.CompletedTask,
                new HostBridgeDownloadStartOptions<HostBridgeDownloadItem>
                {
                    RegisterCancellation = (id, committed) =>
                    {
                        Assert.True(tracker.Remove(id, deleteData: false, out var removed));
                        Assert.Same(committed, removed);
                        replacement = new HostBridgeDownloadItem { DownloadId = id, Title = "replacement" };
                        Assert.True(tracker.TryAddAttempt(replacement).Applied);
                        throw new InvalidOperationException("registration failed after replacement");
                    },
                }));

        Assert.Equal("registration failed after replacement", error.Message);
        Assert.NotNull(replacement);
        Assert.True(tracker.TryGet(replacement!.DownloadId, out var current));
        Assert.Same(replacement, current);
        Assert.Contains("replacement", File.ReadAllText(persistencePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegistrationFailure_DoesNotRollbackAttemptWhoseRevisionChangedReentrantly()
    {
        var tracker = V2Store();
        HostBridgeDownloadItem? transitioned = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new HostBridgeDownloadOrchestrator().StartTrackedDownloadV2Async(
                new Settings(), tracker,
                static settings => new Settings { Value = settings.Value },
                static (_, id) => new HostBridgeDownloadItem { DownloadId = id },
                static (_, _, _) => ValueTask.FromResult(HostBridgePreAdmissionResult.Allow()),
                static (_, _, _, _) => Task.CompletedTask,
                new HostBridgeDownloadStartOptions<HostBridgeDownloadItem>
                {
                    RegisterCancellation = (id, committed) =>
                    {
                        transitioned = committed;
                        var key = new HostBridgeQueueMutationKey(id, committed.AttemptId, committed.Revision);
                        Assert.True(tracker.TryTransition(key, HostBridgeDownloadAttemptState.Preparing).Applied);
                        throw new InvalidOperationException("registration failed after transition");
                    },
                }));

        Assert.NotNull(transitioned);
        Assert.True(tracker.TryGet(transitioned!.DownloadId, out var current));
        Assert.Same(transitioned, current);
        Assert.Equal(HostBridgeDownloadAttemptState.Preparing, current!.AttemptState);
        Assert.Equal(2, current.Revision);
    }

    [Fact]
    public async Task V2CallerCancellationAfterCommit_StillRunsDelegateAndPersistsTerminalMutation()
    {
        Directory.CreateDirectory(_tempDirectory);
        var persistencePath = Path.Combine(_tempDirectory, "cancel-after-commit.json");
        var tracker = V2Store(persistencePath);
        using var callerCancellation = new CancellationTokenSource();
        var finalPersistence = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var orchestrator = new HostBridgeDownloadOrchestrator
        {
            FinalPersistenceCompleted = id => finalPersistence.TrySetResult(id),
        };

        var downloadId = await orchestrator.StartTrackedDownloadV2Async(
            new Settings(), tracker,
            static settings => new Settings { Value = settings.Value },
            static (_, id) => new HostBridgeDownloadItem { DownloadId = id },
            static (_, _, _) => ValueTask.FromResult(HostBridgePreAdmissionResult.Allow()),
            (_, _, item, token) =>
            {
                tokenObserved.TrySetResult(token.IsCancellationRequested);
                var queued = new HostBridgeQueueMutationKey(item.DownloadId, item.AttemptId, item.Revision);
                var cancelling = tracker.TryTransition(queued, HostBridgeDownloadAttemptState.Cancelling);
                Assert.True(cancelling.Applied);
                Assert.True(tracker.TryTransition(cancelling.Current, HostBridgeDownloadAttemptState.Cancelled).Applied);
                item.SetStatus(HostBridgeDownloadItemStatus.Cancelled);
                item.CompletedAt = DateTime.UtcNow;
                return Task.CompletedTask;
            },
            new HostBridgeDownloadStartOptions<HostBridgeDownloadItem>
            {
                RegisterCancellation = (_, _) =>
                {
                    callerCancellation.Cancel();
                    return null!;
                },
            },
            callerCancellation.Token);

        Assert.True(await tokenObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(downloadId, await finalPersistence.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        using (var persisted = JsonDocument.Parse(File.ReadAllText(persistencePath)))
        {
            var persistedItem = persisted.RootElement.GetProperty("items").EnumerateArray()
                .Single(element => string.Equals(
                    element.GetProperty("downloadId").GetString(),
                    downloadId,
                    StringComparison.OrdinalIgnoreCase));
            Assert.Equal("Cancelled", persistedItem.GetProperty("status").GetString());
            Assert.Equal("Cancelled", persistedItem.GetProperty("attemptState").GetString());
        }
        Assert.True(tracker.TryGet(downloadId, out var item));
        Assert.Equal(HostBridgeDownloadItemStatus.Cancelled, item!.GetStatus());
        Assert.Equal(HostBridgeDownloadAttemptState.Cancelled, item.AttemptState);
    }

    [Fact]
    public async Task RollbackWarningObserverFailure_IsReportedWithoutReplacingRegistrationFailure()
    {
        Directory.CreateDirectory(_tempDirectory);
        var persistencePath = Path.Combine(_tempDirectory, "rollback-warning.json");
        var tracker = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            persistencePath: persistencePath,
            onWarn: _ => throw new InvalidOperationException("rollback warning observer failed"),
            options: new HostBridgeQueueStoreOptions
            {
                ContractVersion = HostBridgeQueueContractVersion.AttemptV2,
            });
        var existing = new HostBridgeDownloadItem { DownloadId = "existing" };
        Assert.True(tracker.TryAddAttempt(existing).Applied);
        var reported = new ConcurrentQueue<string>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new HostBridgeDownloadOrchestrator().StartTrackedDownloadV2Async(
                new Settings(), tracker,
                static settings => new Settings { Value = settings.Value },
                static (_, id) => new HostBridgeDownloadItem { DownloadId = id },
                static (_, _, _) => ValueTask.FromResult(HostBridgePreAdmissionResult.Allow()),
                static (_, _, _, _) => Task.CompletedTask,
                new HostBridgeDownloadStartOptions<HostBridgeDownloadItem>
                {
                    OnBackgroundFault = (_, exception) => reported.Enqueue(exception.Message),
                    RegisterCancellation = (_, _) =>
                    {
                        existing.SetProgress(double.NaN);
                        throw new InvalidOperationException("original registration failure");
                    },
                }));

        Assert.Equal("original registration failure", error.Message);
        Assert.Contains("rollback warning observer failed", reported);
        Assert.Single(tracker.GetSnapshot());
        Assert.Same(existing, tracker.GetSnapshot().Single());
    }

    [Fact]
    public async Task BackgroundCleanupFaults_AreReportedIndependentlyAndDoNotStopLaterCleanup()
    {
        Directory.CreateDirectory(_tempDirectory);
        var persistencePath = Path.Combine(_tempDirectory, "cleanup-faults.json");
        var tracker = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            persistencePath: persistencePath,
            onWarn: _ => throw new InvalidOperationException("warning observer failed"),
            options: new HostBridgeQueueStoreOptions
            {
                ContractVersion = HostBridgeQueueContractVersion.AttemptV2,
            });
        var faults = new ConcurrentQueue<string>();
        var allFaultsReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var orchestrator = new HostBridgeDownloadOrchestrator
        {
            FinalPersistenceCompleted = _ => throw new InvalidOperationException("final observer failed"),
        };

        _ = await orchestrator.StartTrackedDownloadV2Async(
            new Settings(), tracker,
            static settings => new Settings { Value = settings.Value },
            static (_, id) => new HostBridgeDownloadItem { DownloadId = id },
            static (_, _, _) => ValueTask.FromResult(HostBridgePreAdmissionResult.Allow()),
            static (_, _, item, _) =>
            {
                item.SetProgress(double.NaN);
                return Task.CompletedTask;
            },
            new HostBridgeDownloadStartOptions<HostBridgeDownloadItem>
            {
                OnBackgroundFault = (_, error) =>
                {
                    faults.Enqueue(error.Message);
                    if (faults.Contains("warning observer failed") &&
                        faults.Contains("final observer failed") &&
                        faults.Contains("registration dispose failed"))
                    {
                        allFaultsReported.TrySetResult();
                    }
                    throw new InvalidOperationException("fault observer failed");
                },
                RegisterCancellation = (_, _) => new HostBridgeDownloadCancellationRegistration(
                    CancellationToken.None,
                    () =>
                    {
                        throw new InvalidOperationException("registration dispose failed");
                    }),
            });

        await allFaultsReported.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("warning observer failed", faults);
        Assert.Contains("final observer failed", faults);
        Assert.Contains("registration dispose failed", faults);
    }

    [Fact]
    public async Task BackgroundWrapperFault_IsContainedAndNeverBecomesUnobserved()
    {
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unobserved = new ConcurrentQueue<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, args) =>
        {
            foreach (var exception in args.Exception.Flatten().InnerExceptions)
                unobserved.Enqueue(exception);
        };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            _ = await new HostBridgeDownloadOrchestrator(new ThrowingLogger()).StartTrackedDownloadV2Async(
                new Settings(), V2Store(),
                static settings => new Settings { Value = settings.Value },
                static (_, id) => new HostBridgeDownloadItem { DownloadId = id },
                static (_, _, _) => ValueTask.FromResult(HostBridgePreAdmissionResult.Allow()),
                static (_, _, _, _) => throw new InvalidOperationException("work failed"),
                new HostBridgeDownloadStartOptions<HostBridgeDownloadItem>
                {
                    OnBackgroundFault = (_, error) =>
                    {
                        if (error.Message == "logger failed")
                        {
                            reported.TrySetResult();
                        }
                    },
                });

            // "logger failed" escapes the work delegate and is reported only by the
            // fault-only continuation after that continuation reads faultedTask.Exception.
            await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var pass = 0; pass < 3; pass++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.DoesNotContain(unobserved, error => error.Message.Contains("logger failed", StringComparison.Ordinal));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
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
        Directory.CreateDirectory(_tempDirectory);
        var persistencePath = Path.Combine(_tempDirectory, "stress-ordering.json");
        var tracker = V2Store(persistencePath);
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
                    try
                    {
                        if (!tracker.TryGet(id, out _)) orderingViolations.Enqueue("work-before-commit");
                        if (!File.ReadAllText(persistencePath).Contains(id, StringComparison.OrdinalIgnoreCase))
                            orderingViolations.Enqueue("work-before-persist");
                        if (!registered.ContainsKey(id)) orderingViolations.Enqueue("work-before-register");
                    }
                    catch (Exception ex)
                    {
                        orderingViolations.Enqueue("work-observation-failed: " + ex.Message);
                    }
                    finally
                    {
                        if (Interlocked.Increment(ref workStartedCount) == attemptCount)
                            allWorkStarted.TrySetResult();
                    }
                    return Task.CompletedTask;
                },
                new HostBridgeDownloadStartOptions<HostBridgeDownloadItem>
                {
                    RegisterCancellation = (id, item) =>
                    {
                        Assert.True(tracker.TryGet(id, out _));
                        Assert.Contains(id, File.ReadAllText(persistencePath), StringComparison.OrdinalIgnoreCase);
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
