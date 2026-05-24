using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.HostBridge;

/// <summary>
/// Centralises the fire-and-forget download enqueue pattern shared by every RicherTunes
/// streaming-service plugin's <c>Download(...)</c> method.
///
/// <para><strong>Shape replaced in each plugin (Wave A item 2):</strong></para>
/// <list type="number">
///   <item>Snapshot settings before <c>Task.Run</c> — prevents TOCTOU when settings change
///         mid-download (the "ProbeOnly read twice" race documented in PR #130 finding #9).</item>
///   <item>Generate a GUID <c>downloadId</c> (<c>Guid.NewGuid().ToString("N")</c>).</item>
///   <item>Construct a tracked download item via caller-supplied <paramref name="itemFactory"/>.</item>
///   <item>Insert into <paramref name="tracker"/> before Task.Run, so <c>GetItems()</c>
///         polling never misses a just-started download.</item>
///   <item>Fire-and-forget <c>Task.Run</c> the actual work. The work lambda receives the
///         SNAPSHOT, not the live settings object — defeating the race.</item>
///   <item>Return <c>downloadId</c>.</item>
/// </list>
///
/// <para><strong>Snapshot strategy</strong>: option (c) — caller supplies a
/// <paramref name="snapshotter"/> lambda. This is zero-magic and explicit about which fields
/// are included in the snapshot. Per-plugin call sites pass
/// <c>s => new TSettings { Field1 = s.Field1, … }</c> or call a <c>Clone()</c> method if
/// one exists. This is especially important for settings that hold reference types (lists,
/// dicts): the snapshotter is responsible for deep-copying those fields.</para>
///
/// <para>Lifted from Tidalarr and AppleMusicarr as Wave A item 2 of the May 2026
/// bridge-unification plan.</para>
/// </summary>
public sealed class HostBridgeDownloadOrchestrator
{
    private readonly ILogger? _logger;

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

        // Step 5: fire-and-forget. Captures snapshot (not live settings), downloadId, and item.
        _ = Task.Run(async () =>
        {
            try
            {
                await doWork(snapshot, downloadId, item, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
        }, cancellationToken);

        // Step 6: return immediately — doWork is still running in the background.
        return Task.FromResult(downloadId);
    }
}
