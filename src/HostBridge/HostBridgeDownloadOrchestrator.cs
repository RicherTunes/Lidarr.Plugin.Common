using System;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Utilities;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.HostBridge;

/// <summary>
/// Per-download cancellation registration owned by <see cref="HostBridgeDownloadOrchestrator"/>.
/// Plugins use this to expose an externally cancellable token for a queued download and to
/// remove/dispose any registry entry when the background work exits.
/// </summary>
public sealed class HostBridgeDownloadCancellationRegistration : IDisposable
{
    private readonly Action? _dispose;
    private int _disposed;

    public HostBridgeDownloadCancellationRegistration(CancellationToken token, Action? dispose = null)
    {
        Token = token;
        _dispose = dispose;
    }

    public CancellationToken Token { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _dispose?.Invoke();
        }
    }
}

/// <summary>
/// Optional behaviors for <see cref="HostBridgeDownloadOrchestrator.StartTrackedDownloadAsync{TItem,TSettings}(TSettings, HostBridgeDownloadTrackerStore{TItem}, Func{TSettings,TSettings}, Func{TSettings,string,TItem}, Func{TSettings,string,TItem,CancellationToken,Task}, HostBridgeDownloadStartOptions{TItem}, CancellationToken)"/>.
/// </summary>
public sealed class HostBridgeDownloadStartOptions<TItem>
    where TItem : HostBridgeDownloadItem
{
    /// <summary>
    /// Called after the tracker item is created and inserted, before the background task is
    /// scheduled. The returned token is linked with the caller token, and the registration is
    /// disposed by Common when the background work exits.
    /// </summary>
    public Func<string, TItem, HostBridgeDownloadCancellationRegistration>? RegisterCancellation { get; init; }

    /// <summary>
    /// Observes cleanup, observer, and wrapper-infrastructure failures contained by the
    /// fire-and-forget path. Work-delegate exceptions retain their existing logger-only
    /// handling. The callback is invoked outside tracker locks, and callback failures are ignored.
    /// </summary>
    public Action<string, Exception>? OnBackgroundFault { get; init; }
}

/// <summary>
/// Centralises the fire-and-forget download enqueue pattern shared by every RicherTunes
/// streaming-service plugin's <c>Download(...)</c> method.
///
/// <para><strong>Shape replaced in each plugin (Wave A item 2):</strong></para>
/// <list type="number">
///   <item>Snapshot settings before <c>Task.Run</c> — prevents TOCTOU when settings change
///         mid-download (the "ProbeOnly read twice" race documented in PR #130 finding #9).</item>
///   <item>Generate a GUID <c>downloadId</c> (<c>Guid.NewGuid().ToString("N")</c>).</item>
///   <item>Construct a tracked download item via caller-supplied item factory.</item>
///   <item>Insert into the tracker before Task.Run, so <c>GetItems()</c>
///         polling never misses a just-started download.</item>
///   <item>Fire-and-forget <c>Task.Run</c> the actual work. The work lambda receives the
///         SNAPSHOT, not the live settings object — defeating the race.</item>
///   <item>Return <c>downloadId</c>.</item>
/// </list>
///
/// <para><strong>Snapshot strategy</strong>: settings that only contain primitive,
/// enum, nullable, and string values can use the overloads that call
/// <see cref="SettingsSnapshot.Copy{TSettings}(TSettings)"/>. Settings with mutable
/// reference-typed fields should keep using the explicit snapshotter overloads so the
/// caller can deep-copy those fields.</para>
///
/// <para>Lifted from Tidalarr and AppleMusicarr as Wave A item 2 of the May 2026
/// bridge-unification plan.</para>
/// </summary>
public sealed class HostBridgeDownloadOrchestrator
{
    private readonly ILogger? _logger;

    internal Action<string>? FinalPersistenceCompleted { get; set; }

    /// <summary>
    /// Create an orchestrator instance. <paramref name="logger"/> is optional — pass
    /// <see langword="null"/> to silence orchestrator-level log output (useful in tests or
    /// when the plugin's own logger already covers the surrounding context).
    /// </summary>
    public HostBridgeDownloadOrchestrator(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Snapshot settings with <see cref="SettingsSnapshot.Copy{TSettings}(TSettings)"/>,
    /// create a tracked item, enqueue fire-and-forget work, return downloadId.
    /// </summary>
    /// <typeparam name="TItem">Per-plugin item type extending <see cref="HostBridgeDownloadItem"/> (or
    /// <see cref="HostBridgeDownloadItem"/> itself).</typeparam>
    /// <typeparam name="TSettings">Per-plugin settings type with a public parameterless constructor.
    /// Public read-write non-indexer properties are copied into the snapshot.</typeparam>
    /// <param name="settings">Live settings object to snapshot before Task.Run.</param>
    /// <param name="tracker">Process-wide tracker store. The item is inserted BEFORE Task.Run
    /// so GetItems() polling never races against a just-started download.</param>
    /// <param name="itemFactory">Creates the tracker item. Receives the SNAPSHOT and the
    /// generated downloadId. Called synchronously before Task.Run.</param>
    /// <param name="doWork">The actual download logic. Receives the SNAPSHOT, downloadId,
    /// the created item (for progress updates), and the cancellation token.
    /// Executed fire-and-forget on <c>Task.Run</c>.</param>
    /// <param name="cancellationToken">Token forwarded into <paramref name="doWork"/>.</param>
    /// <returns>The generated downloadId (32 hex characters, no hyphens).</returns>
    public Task<string> StartTrackedDownloadAsync<TItem, TSettings>(
        TSettings settings,
        HostBridgeDownloadTrackerStore<TItem> tracker,
        Func<TSettings, string, TItem> itemFactory,
        Func<TSettings, string, TItem, CancellationToken, Task> doWork,
        CancellationToken cancellationToken = default)
        where TItem : HostBridgeDownloadItem
        where TSettings : class, new()
        => StartTrackedDownloadAsyncCore(
            settings,
            tracker,
            SettingsSnapshot.Copy<TSettings>,
            itemFactory,
            doWork,
            options: null,
            cancellationToken);

    /// <summary>
    /// Snapshot settings with <see cref="SettingsSnapshot.Copy{TSettings}(TSettings)"/>,
    /// create a tracked item, enqueue fire-and-forget work, and return the downloadId,
    /// with optional per-download cancellation registration.
    /// </summary>
    public Task<string> StartTrackedDownloadAsync<TItem, TSettings>(
        TSettings settings,
        HostBridgeDownloadTrackerStore<TItem> tracker,
        Func<TSettings, string, TItem> itemFactory,
        Func<TSettings, string, TItem, CancellationToken, Task> doWork,
        HostBridgeDownloadStartOptions<TItem> options,
        CancellationToken cancellationToken = default)
        where TItem : HostBridgeDownloadItem
        where TSettings : class, new()
    {
        if (options is null) throw new ArgumentNullException(nameof(options));

        return StartTrackedDownloadAsyncCore(
            settings,
            tracker,
            SettingsSnapshot.Copy<TSettings>,
            itemFactory,
            doWork,
            options,
            cancellationToken);
    }

    /// <summary>
    /// Snapshot settings, create a tracked item, enqueue fire-and-forget work, return downloadId.
    /// </summary>
    /// <typeparam name="TItem">Per-plugin item type extending <see cref="HostBridgeDownloadItem"/> (or
    /// <see cref="HostBridgeDownloadItem"/> itself).</typeparam>
    /// <typeparam name="TSettings">Per-plugin settings type. The caller provides the
    /// <paramref name="snapshotter"/> so this method is agnostic to whether TSettings has
    /// <c>ICloneable</c>, a copy-constructor, or deep-clone logic for reference-typed fields.</typeparam>
    /// <param name="settings">Live settings object to snapshot before Task.Run.</param>
    /// <param name="tracker">Process-wide tracker store. The item is inserted BEFORE Task.Run
    /// so GetItems() polling never races against a just-started download.</param>
    /// <param name="snapshotter">Pure function that produces an isolated copy of
    /// <paramref name="settings"/>. Must copy all fields the <paramref name="doWork"/> lambda
    /// reads; reference-typed fields (lists, dicts) must be deep-copied.</param>
    /// <param name="itemFactory">Creates the tracker item. Receives the SNAPSHOT and the
    /// generated downloadId. Called synchronously before Task.Run.</param>
    /// <param name="doWork">The actual download logic. Receives the SNAPSHOT, downloadId,
    /// the created item (for progress updates), and the cancellation token.
    /// Executed fire-and-forget on <c>Task.Run</c>.</param>
    /// <param name="cancellationToken">Token forwarded into <paramref name="doWork"/>.</param>
    /// <returns>The generated downloadId (32 hex characters, no hyphens).</returns>
    public Task<string> StartTrackedDownloadAsync<TItem, TSettings>(
        TSettings settings,
        HostBridgeDownloadTrackerStore<TItem> tracker,
        Func<TSettings, TSettings> snapshotter,
        Func<TSettings, string, TItem> itemFactory,
        Func<TSettings, string, TItem, CancellationToken, Task> doWork,
        CancellationToken cancellationToken = default)
        where TItem : HostBridgeDownloadItem
        => StartTrackedDownloadAsyncCore(
            settings,
            tracker,
            snapshotter,
            itemFactory,
            doWork,
            options: null,
            cancellationToken);

    /// <summary>
    /// Snapshot settings, create a tracked item, enqueue fire-and-forget work, and return the
    /// downloadId, with optional per-download cancellation registration.
    /// </summary>
    public Task<string> StartTrackedDownloadAsync<TItem, TSettings>(
        TSettings settings,
        HostBridgeDownloadTrackerStore<TItem> tracker,
        Func<TSettings, TSettings> snapshotter,
        Func<TSettings, string, TItem> itemFactory,
        Func<TSettings, string, TItem, CancellationToken, Task> doWork,
        HostBridgeDownloadStartOptions<TItem> options,
        CancellationToken cancellationToken = default)
        where TItem : HostBridgeDownloadItem
    {
        if (options is null) throw new ArgumentNullException(nameof(options));

        return StartTrackedDownloadAsyncCore(
            settings,
            tracker,
            snapshotter,
            itemFactory,
            doWork,
            options,
            cancellationToken);
    }

    /// <summary>
    /// Snapshot settings and perform asynchronous pre-admission before committing an AttemptV2
    /// queue item, registering cancellation, or scheduling background work.
    /// </summary>
    public async Task<string> StartTrackedDownloadV2Async<TItem, TSettings>(
        TSettings settings,
        HostBridgeDownloadTrackerStore<TItem> tracker,
        Func<TSettings, TSettings> snapshotter,
        Func<TSettings, string, TItem> itemFactory,
        Func<TSettings, string, TItem, ValueTask<HostBridgePreAdmissionResult>> preAdmission,
        Func<TSettings, string, TItem, CancellationToken, Task> doWork,
        HostBridgeDownloadStartOptions<TItem> options,
        CancellationToken cancellationToken = default)
        where TItem : HostBridgeDownloadItem
    {
        if (tracker is null) throw new ArgumentNullException(nameof(tracker));
        if (snapshotter is null) throw new ArgumentNullException(nameof(snapshotter));
        if (itemFactory is null) throw new ArgumentNullException(nameof(itemFactory));
        if (preAdmission is null) throw new ArgumentNullException(nameof(preAdmission));
        if (doWork is null) throw new ArgumentNullException(nameof(doWork));
        if (options is null) throw new ArgumentNullException(nameof(options));

        TSettings snapshot = snapshotter(settings);
        string downloadId = Guid.NewGuid().ToString("N");
        TItem item = itemFactory(snapshot, downloadId);

        HostBridgePreAdmissionResult admission =
            await preAdmission(snapshot, downloadId, item).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!admission.Accepted)
        {
            throw new HostBridgePreAdmissionException(admission.Code, admission.Message);
        }

        var commit = tracker.TryAddAttempt(item);
        if (!commit.Applied)
        {
            throw new InvalidOperationException(HostBridgeQueueResultCodes.Conflict);
        }

        return await ScheduleTrackedWorkAsync(
                snapshot,
                downloadId,
                item,
                tracker,
                doWork,
                options,
                cancellationToken,
                commit.Current)
            .ConfigureAwait(false);
    }

    private Task<string> StartTrackedDownloadAsyncCore<TItem, TSettings>(
        TSettings settings,
        HostBridgeDownloadTrackerStore<TItem> tracker,
        Func<TSettings, TSettings> snapshotter,
        Func<TSettings, string, TItem> itemFactory,
        Func<TSettings, string, TItem, CancellationToken, Task> doWork,
        HostBridgeDownloadStartOptions<TItem>? options,
        CancellationToken cancellationToken)
        where TItem : HostBridgeDownloadItem
    {
        if (tracker is null) throw new ArgumentNullException(nameof(tracker));
        if (snapshotter is null) throw new ArgumentNullException(nameof(snapshotter));
        if (itemFactory is null) throw new ArgumentNullException(nameof(itemFactory));
        if (doWork is null) throw new ArgumentNullException(nameof(doWork));

        // Step 1: snapshot settings synchronously, before entering Task.Run.
        // This is the ProbeOnly-race fix: any field the doWork lambda reads comes from the
        // snapshot, not from the live settings object that the user might change mid-download.
        TSettings snapshot = snapshotter(settings);

        // Step 2: generate a unique, URL-safe download identifier.
        string downloadId = Guid.NewGuid().ToString("N");

        // Step 3: construct the tracker item via the caller-supplied factory.
        TItem item = itemFactory(snapshot, downloadId);

        // Step 4: insert into the tracker BEFORE Task.Run so GetItems() polling sees it
        // immediately — no window where the download is "started" but invisible to the queue.
        tracker.AddOrReplace(item);

        _logger?.LogDebug(
            "HostBridgeDownloadOrchestrator: enqueuing download {DownloadId} (tracker count after add: item inserted)",
            downloadId);

        return ScheduleTrackedWorkAsync(
            snapshot,
            downloadId,
            item,
            tracker,
            doWork,
            options,
            cancellationToken);
    }

    private Task<string> ScheduleTrackedWorkAsync<TItem, TSettings>(
        TSettings snapshot,
        string downloadId,
        TItem item,
        HostBridgeDownloadTrackerStore<TItem> tracker,
        Func<TSettings, string, TItem, CancellationToken, Task> doWork,
        HostBridgeDownloadStartOptions<TItem>? options,
        CancellationToken cancellationToken,
        HostBridgeQueueMutationKey? committedAttempt = null)
        where TItem : HostBridgeDownloadItem
    {
        HostBridgeDownloadCancellationRegistration? cancellationRegistration = null;
        try
        {
            cancellationRegistration = options?.RegisterCancellation?.Invoke(downloadId, item);
        }
        catch
        {
            if (committedAttempt.HasValue)
            {
                try
                {
                    tracker.TryRollbackAttempt(committedAttempt.Value, item);
                }
                catch (Exception ex)
                {
                    ReportBackgroundFault(options, downloadId, ex);
                }
            }
            else
                tracker.Remove(downloadId, deleteData: false, out _);
            throw;
        }

        CancellationToken effectiveCancellationToken =
            CreateEffectiveCancellationToken(cancellationToken, cancellationRegistration, out var linkedCancellationSource);

        // AttemptV2 commits must always enter the wrapper so terminal mutation and final
        // persistence cannot be skipped by cancellation after commit. Legacy callers retain
        // their prior behavior: only a non-null registration forces wrapper entry.
        var taskRunCancellationToken = committedAttempt.HasValue || cancellationRegistration is not null
            ? CancellationToken.None
            : cancellationToken;

        // Step 5: fire-and-forget. Captures snapshot (not live settings), downloadId, and item.
        var backgroundTask = Task.Run(async () =>
        {
            try
            {
                await doWork(snapshot, downloadId, item, effectiveCancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (effectiveCancellationToken.IsCancellationRequested)
            {
                _logger?.LogDebug(
                    "HostBridgeDownloadOrchestrator: download {DownloadId} cancelled.",
                    downloadId);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "HostBridgeDownloadOrchestrator: unhandled exception in doWork for download {DownloadId}.",
                    downloadId);
            }
            finally
            {
                try
                {
                    tracker.PersistSnapshot();
                }
                catch (Exception ex)
                {
                    ReportBackgroundFault(options, downloadId, ex);
                }
                try
                {
                    FinalPersistenceCompleted?.Invoke(downloadId);
                }
                catch (Exception ex)
                {
                    ReportBackgroundFault(options, downloadId, ex);
                }
                try
                {
                    linkedCancellationSource?.Dispose();
                }
                catch (Exception ex)
                {
                    ReportBackgroundFault(options, downloadId, ex);
                }
                try
                {
                    cancellationRegistration?.Dispose();
                }
                catch (Exception ex)
                {
                    ReportBackgroundFault(options, downloadId, ex);
                    _logger?.LogWarning(ex,
                        "HostBridgeDownloadOrchestrator: cancellation cleanup failed for download {DownloadId}.",
                        downloadId);
                }
            }
        }, taskRunCancellationToken);

        _ = backgroundTask.ContinueWith(
            faultedTask =>
            {
                var aggregate = faultedTask.Exception;
                if (aggregate is null)
                    return;

                foreach (var exception in aggregate.Flatten().InnerExceptions)
                    ReportBackgroundFault(options, downloadId, exception);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // Step 6: return immediately — doWork is still running in the background.
        return Task.FromResult(downloadId);
    }

    private static void ReportBackgroundFault<TItem>(
        HostBridgeDownloadStartOptions<TItem>? options,
        string downloadId,
        Exception exception)
        where TItem : HostBridgeDownloadItem
    {
        try
        {
            options?.OnBackgroundFault?.Invoke(downloadId, exception);
        }
        catch
        {
            // A diagnostic observer must never fault the fire-and-forget wrapper.
        }
    }

    private static CancellationToken CreateEffectiveCancellationToken(
        CancellationToken callerToken,
        HostBridgeDownloadCancellationRegistration? cancellationRegistration,
        out CancellationTokenSource? linkedCancellationSource)
    {
        linkedCancellationSource = null;
        if (cancellationRegistration is null)
        {
            return callerToken;
        }

        CancellationToken registeredToken = cancellationRegistration.Token;
        if (!callerToken.CanBeCanceled)
        {
            return registeredToken;
        }

        if (!registeredToken.CanBeCanceled)
        {
            return callerToken;
        }

        linkedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
            callerToken,
            registeredToken);
        return linkedCancellationSource.Token;
    }
}
