using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Lidarr.Plugin.Common.HostBridge;

/// <summary>
/// Status enum for <see cref="HostBridgeDownloadItem"/>. Matches the conceptual states
/// every Lidarr download client passes through: Queued → Downloading → Completed/Failed.
///
/// <para>This enum lives in Common so the per-plugin tracker doesn't need to import
/// <c>NzbDrone.Core.Download.DownloadItemStatus</c> in code paths that only need the
/// internal state machine. Plugins map this to the host enum at the boundary
/// (in their <c>DownloadClientBase.GetItems()</c> override).</para>
/// </summary>
public enum HostBridgeDownloadItemStatus
{
    Queued = 0,
    Downloading = 1,
    Completed = 2,
    Failed = 3,
}

/// <summary>
/// Thread-safe per-download tracker DTO. Apple's <c>AppleMusicDownloadTrackerItem</c> and
/// tidal's <c>TidalDownloadItem</c> were byte-for-byte the same pattern (Volatile.Read/Write
/// on int status + Interlocked.Exchange on a long-bit-packed double progress). Lifted to
/// Common as Wave A item 1 from <c>memory/project_apple_bridge_unification_plan.md</c>.
///
/// <para>Plugins subclass this only if they need extra fields beyond what's here
/// (apple/tidal currently don't — they can use this type directly).</para>
/// </summary>
public class HostBridgeDownloadItem
{
    private int _status = (int)HostBridgeDownloadItemStatus.Queued;
    private long _progressBits;

    public string DownloadId { get; init; } = string.Empty;
    public string AlbumId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Total size in bytes when known (some plugins emit size estimates from album metadata).
    /// </summary>
    public long TotalSize { get; set; }

    /// <summary>Thread-safe status read.</summary>
    public HostBridgeDownloadItemStatus GetStatus()
        => (HostBridgeDownloadItemStatus)Volatile.Read(ref _status);

    /// <summary>Thread-safe status write.</summary>
    public void SetStatus(HostBridgeDownloadItemStatus value)
        => Volatile.Write(ref _status, (int)value);

    /// <summary>Thread-safe progress read (double, atomic via bit-pattern Interlocked).</summary>
    public double GetProgress()
        => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _progressBits));

    /// <summary>Thread-safe progress write.</summary>
    public void SetProgress(double value)
        => Interlocked.Exchange(ref _progressBits, BitConverter.DoubleToInt64Bits(value));
}

/// <summary>
/// Process-wide tracker store for <see cref="HostBridgeDownloadItem"/> (or subclass).
///
/// <para>The store is intentionally instance-scoped (not static) so each plugin holds ONE
/// store across all client re-instantiations. Plugins wire it up as a <c>static readonly</c>
/// field on their <c>DownloadClientBase</c> subclass — Lidarr can re-construct the client
/// between queue polls, but the store survives.</para>
///
/// <para>Retention: completed/failed items are evicted from <see cref="GetSnapshot"/> after
/// the configured retention window. In-progress items are NEVER evicted (no upper bound on
/// download duration — a stuck job stays in the queue until the operator removes it).</para>
///
/// <para>Lifted from apple's <c>AppleMusicLidarrDownloadClient.ActiveDownloads</c> +
/// retention sweep (Wave A item 1 of the May 2026 unification plan). Tidalarr's
/// equivalent (TidalLidarrDownloadClient.ActiveDownloads) can adopt the same store
/// once it's available — the security fix from PR #130 review #2 finding #11
/// (path-traversal containment) becomes universal as a side effect.</para>
/// </summary>
public class HostBridgeDownloadTrackerStore<TItem>
    where TItem : HostBridgeDownloadItem
{
    private readonly ConcurrentDictionary<string, TItem> _items = new();
    private readonly TimeSpan _completedRetention;

    /// <summary>
    /// Default retention: 30 minutes. Long enough that the Lidarr UI shows the result
    /// after a download completes; short enough that old failures don't accumulate.
    /// </summary>
    public HostBridgeDownloadTrackerStore(TimeSpan? completedRetention = null)
    {
        _completedRetention = completedRetention ?? TimeSpan.FromMinutes(30);
    }

    /// <summary>Add a tracker item (typically called from <c>Download()</c>).</summary>
    public void Add(TItem item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        if (string.IsNullOrWhiteSpace(item.DownloadId))
            throw new ArgumentException("DownloadId must be non-empty.", nameof(item));

        _items[item.DownloadId] = item;
    }

    /// <summary>
    /// Retrieve a tracker item by DownloadId. Used by the download client's
    /// progress-callback path to update an in-flight item.
    /// </summary>
    public bool TryGet(string downloadId, out TItem? item)
    {
        if (string.IsNullOrWhiteSpace(downloadId))
        {
            item = null;
            return false;
        }
        return _items.TryGetValue(downloadId, out item);
    }

    /// <summary>
    /// Snapshot of currently-tracked items. Side-effect: evicts completed/failed items past
    /// the retention window (FIFO sweep). Plugins call this from
    /// <c>DownloadClientBase.GetItems()</c>.
    /// </summary>
    public IEnumerable<TItem> GetSnapshot()
    {
        var now = DateTime.UtcNow;
        var result = new List<TItem>(_items.Count);

        foreach (var kv in _items)
        {
            var item = kv.Value;
            var status = item.GetStatus();

            if ((status == HostBridgeDownloadItemStatus.Completed ||
                 status == HostBridgeDownloadItemStatus.Failed) &&
                item.CompletedAt.HasValue &&
                now - item.CompletedAt.Value > _completedRetention)
            {
                _items.TryRemove(kv.Key, out _);
                continue;
            }

            result.Add(item);
        }
        return result;
    }

    /// <summary>
    /// Remove a tracker entry. With <paramref name="deleteData"/> set, also delete the
    /// item's <see cref="HostBridgeDownloadItem.OutputPath"/> directory (best-effort).
    /// Returns true if the item was found and removed.
    /// </summary>
    public bool Remove(string downloadId, bool deleteData, out TItem? removed)
    {
        if (!_items.TryRemove(downloadId, out removed))
        {
            return false;
        }
        if (deleteData && removed is not null && !string.IsNullOrWhiteSpace(removed.OutputPath))
        {
            try
            {
                if (Directory.Exists(removed.OutputPath))
                {
                    Directory.Delete(removed.OutputPath, recursive: true);
                }
            }
            catch
            {
                // Best-effort: dir delete failures aren't a tracker concern. Logging happens
                // in the caller which has access to NLog.
            }
        }
        return true;
    }
}
