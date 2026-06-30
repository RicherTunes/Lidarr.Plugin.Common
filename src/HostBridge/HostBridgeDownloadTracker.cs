using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Lidarr.Plugin.Common.Hosting;

namespace Lidarr.Plugin.Common.HostBridge;

/// <summary>
/// Status enum for <see cref="HostBridgeDownloadItem"/>. Matches the conceptual states
/// every Lidarr download client passes through: Queued → Downloading → Completed/Failed/Cancelled.
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
    Cancelled = 4,
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

    // CompletedAt is stored as ticks (long) for atomic reads/writes. DateTime? on x64 is
    // 16 bytes (1 byte HasValue + 7 padding + 8 ticks), and a plain `get/set` is NOT
    // atomic — the retention sweep can observe HasValue=true paired with the previous
    // DateTime's Ticks (or default(DateTime)=0001-01-01), evicting a fresh item as
    // "completed ~21000 days ago". The Interlocked.Read/Exchange pair makes the
    // observation consistent. 0 means "not completed" (DateTime.MinValue.Ticks = 0).
    private long _completedAtTicks;

    public string DownloadId { get; init; } = string.Empty;
    public string AlbumId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Time the download reached a terminal state (Completed / Failed / Cancelled). Backed by an
    /// <c>Interlocked</c>-protected ticks field — safe under concurrent observation from
    /// the retention sweep.
    /// </summary>
    public DateTime? CompletedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _completedAtTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
        set
        {
            // Treat the sentinel value (MinValue) as "not set" too — otherwise a caller
            // writing MinValue would round-trip to null on read, which is confusing.
            var ticks = value.HasValue && value.Value != DateTime.MinValue
                ? value.Value.Ticks
                : 0;
            Interlocked.Exchange(ref _completedAtTicks, ticks);
        }
    }

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
/// JSON-serialisable snapshot of a <see cref="HostBridgeDownloadItem"/>. Used exclusively
/// by the persistence layer inside <see cref="HostBridgeDownloadTrackerStore{TItem}"/>:
/// the in-memory item's atomic/private fields (status, progress, completedAt) are exposed
/// as plain properties here so <c>System.Text.Json</c> can round-trip them without
/// reflection hacks or custom converters.
///
/// <para>Callers outside of tests should not construct this type directly; use
/// <see cref="FromItem"/> and <see cref="ToItem"/> as the conversion boundary.</para>
/// </summary>
public sealed class HostBridgeDownloadItemDto
{
    [JsonPropertyName("downloadId")]
    public string DownloadId { get; set; } = string.Empty;

    [JsonPropertyName("albumId")]
    public string AlbumId { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("artist")]
    public string Artist { get; set; } = string.Empty;

    [JsonPropertyName("outputPath")]
    public string OutputPath { get; set; } = string.Empty;

    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; set; }

    [JsonPropertyName("totalSize")]
    public long TotalSize { get; set; }

    [JsonPropertyName("status")]
    public HostBridgeDownloadItemStatus Status { get; set; }

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    /// <summary>Capture all observable state from <paramref name="item"/> into a DTO.</summary>
    public static HostBridgeDownloadItemDto FromItem(HostBridgeDownloadItem item) => new()
    {
        DownloadId  = item.DownloadId,
        AlbumId     = item.AlbumId,
        Title       = item.Title,
        Artist      = item.Artist,
        OutputPath  = item.OutputPath,
        StartedAt   = item.StartedAt,
        CompletedAt = item.CompletedAt,
        TotalSize   = item.TotalSize,
        Status      = item.GetStatus(),
        Progress    = item.GetProgress(),
    };

    /// <summary>
    /// Reconstruct a base <see cref="HostBridgeDownloadItem"/> from this DTO.
    /// Used by the default item factory in
    /// <see cref="HostBridgeDownloadTrackerStore{TItem}"/> when <c>TItem</c> is
    /// exactly <see cref="HostBridgeDownloadItem"/> (the common case).
    /// </summary>
    public HostBridgeDownloadItem ToItem()
    {
        var item = new HostBridgeDownloadItem
        {
            DownloadId  = DownloadId,
            AlbumId     = AlbumId,
            Title       = Title,
            Artist      = Artist,
            OutputPath  = OutputPath,
            StartedAt   = StartedAt,
            TotalSize   = TotalSize,
            CompletedAt = CompletedAt,
        };
        item.SetStatus(Status);
        item.SetProgress(Progress);
        return item;
    }
}

/// <summary>
/// Process-wide tracker store for <see cref="HostBridgeDownloadItem"/> (or subclass).
///
/// <para>The store is intentionally instance-scoped (not static) so each plugin holds ONE
/// store across all client re-instantiations. Plugins wire it up as a <c>static readonly</c>
/// field on their <c>DownloadClientBase</c> subclass — Lidarr can re-construct the client
/// between queue polls, but the store survives.</para>
///
/// <para><strong>Persistence</strong> (optional): pass <c>persistencePath</c> (or
/// use <see cref="ForPlugin"/>) to enable write-through JSON persistence. Every mutation
/// (add/remove/evict) is flushed atomically (write to <c>.tmp</c>, rename). On construction
/// the file is loaded; expired entries are silently skipped. A corrupt/unreadable file starts
/// empty and emits a warning via <c>onWarn</c> instead of throwing.</para>
///
/// <para><strong>Plugin adoption</strong>: change the static store declaration from
/// <c>new()</c> to <c>ForPlugin("MyPluginName")</c> — one line, zero other wiring.
/// Plugins with <c>TItem</c> subclasses must supply an <c>itemFactory</c> lambda to
/// restore the subclass shape from persisted base fields. Subclass-only fields are not
/// serialized by the Common DTO unless they are derivable in that factory.</para>
///
/// <para><strong>Retention</strong>: completed/failed/cancelled items are evicted from
/// <see cref="GetSnapshot"/> after the configured retention window. In-progress items are
/// process-local only; persisted queued/downloading entries are dropped on construction
/// because the worker task that could complete them does not survive a process restart.</para>
///
/// <para>Lifted from apple's <c>AppleMusicLidarrDownloadClient.ActiveDownloads</c> +
/// retention sweep (Wave A item 1 of the May 2026 unification plan).</para>
/// </summary>
public sealed class HostBridgeDownloadTrackerStore<TItem>
    where TItem : HostBridgeDownloadItem
{
    private readonly ConcurrentDictionary<string, TItem> _items = new();
    private readonly TimeSpan _completedRetention;
    private readonly string? _persistencePath;
    private readonly Func<HostBridgeDownloadItemDto, TItem> _itemFactory;
    private readonly Action<string>? _onWarn;
    private readonly object _persistLock = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Default retention: 30 minutes. Long enough that the Lidarr UI shows the result
    /// after a download completes; short enough that old failures don't accumulate.
    /// </summary>
    /// <param name="completedRetention">
    /// How long completed/failed/cancelled entries are retained before being evicted by
    /// <see cref="GetSnapshot"/>. Defaults to 30 minutes.
    /// </param>
    /// <param name="persistencePath">
    /// Optional path to the JSON persistence file. When supplied, the store writes through
    /// on every mutation and reloads on construction. When <see langword="null"/> (default)
    /// the store is purely in-memory (backward-compatible). Use <see cref="ForPlugin"/> for
    /// automatic path resolution via <see cref="PluginConfigRoots"/>.
    /// </param>
    /// <param name="itemFactory">
    /// Factory to reconstruct a <typeparamref name="TItem"/> from a persisted DTO. Required
    /// when <typeparamref name="TItem"/> is a subclass. Defaults to a cast from the base
    /// <see cref="HostBridgeDownloadItem"/> — valid when <typeparamref name="TItem"/> IS
    /// <see cref="HostBridgeDownloadItem"/>.
    /// </param>
    /// <param name="onWarn">
    /// Optional callback invoked with a human-readable message when the persistence file
    /// cannot be loaded (corruption, partial write, etc.). The store starts empty. Without
    /// this callback, corruption is silently swallowed so the plugin continues to function.
    /// </param>
    public HostBridgeDownloadTrackerStore(
        TimeSpan? completedRetention = null,
        string? persistencePath = null,
        Func<HostBridgeDownloadItemDto, TItem>? itemFactory = null,
        Action<string>? onWarn = null)
    {
        _completedRetention = completedRetention ?? TimeSpan.FromMinutes(30);
        _persistencePath    = persistencePath;
        _onWarn             = onWarn;
        _itemFactory        = itemFactory ?? DefaultItemFactory;

        if (_persistencePath != null)
            LoadFromDisk();
    }

    // ─── Static factory ───────────────────────────────────────────────────────

    /// <summary>
    /// Create a persistent store whose JSON file lives in the standard config directory for
    /// <paramref name="pluginName"/> as resolved by <see cref="PluginConfigRoots.Resolve(string)"/>.
    /// The file is named <c>download-tracker.json</c>. The config directory is created on first
    /// write if it doesn't already exist.
    ///
    /// <para>This is the zero-boilerplate adoption path for plugins that use
    /// <see cref="HostBridgeDownloadItem"/> directly (the common case).</para>
    ///
    /// <example>
    /// Change the existing static store declaration:
    /// <code>
    /// // Before:
    /// private static readonly HostBridgeDownloadTrackerStore&lt;HostBridgeDownloadItem&gt; _tracker = new();
    /// // After (one line):
    /// private static readonly HostBridgeDownloadTrackerStore&lt;HostBridgeDownloadItem&gt; _tracker =
    ///     HostBridgeDownloadTrackerStore&lt;HostBridgeDownloadItem&gt;.ForPlugin("QobuzArr");
    /// </code>
    /// </example>
    /// </summary>
    /// <param name="pluginName">Plugin name passed to <see cref="PluginConfigRoots.Resolve(string)"/>.</param>
    /// <param name="completedRetention">Optional retention override; defaults to 30 minutes.</param>
    /// <param name="itemFactory">
    /// Optional item factory for subclass plugins. Not needed when <typeparamref name="TItem"/>
    /// is <see cref="HostBridgeDownloadItem"/>.
    /// </param>
    /// <param name="onWarn">
    /// Optional warning callback forwarded to the store constructor.
    /// </param>
    public static HostBridgeDownloadTrackerStore<TItem> ForPlugin(
        string pluginName,
        TimeSpan? completedRetention = null,
        Func<HostBridgeDownloadItemDto, TItem>? itemFactory = null,
        Action<string>? onWarn = null)
    {
        if (string.IsNullOrWhiteSpace(pluginName))
            throw new ArgumentException("Plugin name must be non-empty.", nameof(pluginName));
        if (pluginName is "." or ".." ||
            Path.IsPathRooted(pluginName) ||
            pluginName.Contains('/') ||
            pluginName.Contains('\\') ||
            pluginName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Plugin name must be a single directory name.", nameof(pluginName));
        }

        var configRoot = PluginConfigRoots.Resolve(pluginName);
        var path       = Path.Combine(configRoot, "download-tracker.json");
        return new HostBridgeDownloadTrackerStore<TItem>(
            completedRetention: completedRetention,
            persistencePath:    path,
            itemFactory:        itemFactory,
            onWarn:             onWarn);
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Add or replace a tracker item (typically called from <c>Download()</c>). Silently
    /// overwrites an existing item with the same <see cref="HostBridgeDownloadItem.DownloadId"/>
    /// — callers that want collision detection should use <see cref="TryAdd"/>.
    /// </summary>
    public void AddOrReplace(TItem item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        if (string.IsNullOrWhiteSpace(item.DownloadId))
            throw new ArgumentException("DownloadId must be non-empty.", nameof(item));

        _items[item.DownloadId] = item;
        PersistToDisk();
    }

    /// <summary>
    /// Legacy alias for <see cref="AddOrReplace"/> kept for source compatibility with the
    /// first lift commit. New callers should prefer <see cref="AddOrReplace"/> or
    /// <see cref="TryAdd"/> for explicit collision intent.
    /// </summary>
    [Obsolete("Use AddOrReplace (overwrite semantics) or TryAdd (collision-aware) instead. This alias will be removed in 2.0.")]
    public void Add(TItem item) => AddOrReplace(item);

    /// <summary>
    /// Insert iff no existing entry has the same DownloadId. Returns true if added.
    /// Use this when the caller wants to detect ID collisions explicitly (e.g. host
    /// restart with stale in-memory state).
    /// </summary>
    public bool TryAdd(TItem item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        if (string.IsNullOrWhiteSpace(item.DownloadId))
            throw new ArgumentException("DownloadId must be non-empty.", nameof(item));

        var added = _items.TryAdd(item.DownloadId, item);
        if (added)
            PersistToDisk();
        return added;
    }

    /// <summary>
    /// Retrieve a tracker item by DownloadId. Used by the download client's
    /// progress-callback path to update an in-flight item.
    /// </summary>
    public bool TryGet(string downloadId, [NotNullWhen(true)] out TItem? item)
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
        var now    = DateTime.UtcNow;
        var result = new List<TItem>(_items.Count);
        var evicted = false;

        foreach (var kv in _items)
        {
            var item   = kv.Value;
            var status = item.GetStatus();

            if (IsTerminalStatus(status) &&
                item.CompletedAt.HasValue &&
                now - item.CompletedAt.Value > _completedRetention)
            {
                _items.TryRemove(kv.Key, out _);
                evicted = true;
                continue;
            }

            result.Add(item);
        }

        if (evicted)
            PersistToDisk();

        return result;
    }

    /// <summary>
    /// Remove a tracker entry. With <paramref name="deleteData"/> set, also delete the
    /// item's <see cref="HostBridgeDownloadItem.OutputPath"/> directory.
    ///
    /// If the directory delete fails (UnauthorizedAccessException, file locked by AV/indexer,
    /// PathTooLongException, …) the exception is reported via the optional
    /// <paramref name="onDeleteError"/> callback so the caller can log it through its NLog
    /// instance. Without the callback, errors are swallowed silently — matches the
    /// pre-v1.9.0 behavior for callers that haven't migrated yet.
    ///
    /// Returns true if the item was found and removed from the dictionary, regardless of
    /// whether the directory delete succeeded.
    /// </summary>
    public bool Remove(string downloadId, bool deleteData, out TItem? removed, Action<Exception>? onDeleteError = null)
    {
        if (!_items.TryRemove(downloadId, out removed))
        {
            return false;
        }

        PersistToDisk();

        if (deleteData && removed is not null && !string.IsNullOrWhiteSpace(removed.OutputPath))
        {
            // Cross-attempt re-grab guard. When the host re-grabs a failed album it queues a NEW
            // download into the SAME OutputPath while the old item is being removed. Deleting the
            // directory here would nuke the new attempt's in-flight files (on POSIX, recursive delete
            // unlinks files even while a FileStream holds them), failing the new attempt → another
            // re-grab → infinite loop (observed live on Qobuz). Skip the delete when any other
            // tracked download is still active (Queued/Downloading) at the same path; that download
            // now owns the directory lifecycle. (removed was already taken out of _items above.)
            foreach (var kvp in _items)
            {
                var other = kvp.Value;
                if (other is null || string.IsNullOrWhiteSpace(other.OutputPath))
                {
                    continue;
                }
                if (SameDirectory(other.OutputPath, removed.OutputPath) && IsActiveStatus(other.GetStatus()))
                {
                    // Another active download owns this path — leave its files intact.
                    return true;
                }
            }

            try
            {
                if (Directory.Exists(removed.OutputPath))
                {
                    Directory.Delete(removed.OutputPath, recursive: true);
                }
            }
            catch (Exception ex) when (onDeleteError is not null)
            {
                // Caller-supplied handler — typically logs via NLog with the plugin's logger.
                try { onDeleteError(ex); } catch { /* handler itself shouldn't break Remove */ }
            }
            catch
            {
                // No handler supplied; swallow silently. Documented as legacy behavior so
                // existing call sites that don't pass onDeleteError don't change behavior.
            }
        }
        return true;
    }

    /// <summary>
    /// Persist the current in-memory tracker state immediately. No-op for stores created
    /// without a <c>persistencePath</c>.
    ///
    /// <para>Call this after mutating a tracked item in-place when the mutation happens
    /// outside Common's <see cref="HostBridgeDownloadOrchestrator"/>. Add/remove/eviction
    /// already flush automatically.</para>
    /// </summary>
    public void PersistSnapshot() => PersistToDisk();

    // ─── Persistence internals ────────────────────────────────────────────────

    private static TItem DefaultItemFactory(HostBridgeDownloadItemDto dto)
    {
        // Correct for the common case where TItem == HostBridgeDownloadItem.
        // Plugins with a TItem subclass MUST supply an itemFactory to the constructor
        // (or ForPlugin) so the restored item has the intended runtime type.
        if (typeof(TItem) != typeof(HostBridgeDownloadItem))
        {
            throw new InvalidOperationException(
                $"Cannot restore {typeof(TItem).Name} from a base-class DTO without a custom itemFactory. " +
                "Pass itemFactory: dto => new MyItem { ... } to the HostBridgeDownloadTrackerStore constructor.");
        }
        return (TItem)dto.ToItem();
    }

    private static bool IsTerminalStatus(HostBridgeDownloadItemStatus status)
        => status is HostBridgeDownloadItemStatus.Completed
            or HostBridgeDownloadItemStatus.Failed
            or HostBridgeDownloadItemStatus.Cancelled;

    private static bool IsActiveStatus(HostBridgeDownloadItemStatus status)
        => status is HostBridgeDownloadItemStatus.Queued
            or HostBridgeDownloadItemStatus.Downloading;

    // OS-aware same-directory comparison for the cross-attempt cleanup guard. Canonicalizes
    // separator and "."/".." spellings, then compares case-sensitively on Linux (the production
    // host target) and case-insensitively elsewhere (Windows/macOS default).
    private static bool SameDirectory(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        try
        {
            return string.Equals(NormalizeDirectoryPath(a), NormalizeDirectoryPath(b), PathComparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return false;
        }
    }

    private static string NormalizeDirectoryPath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private void LoadFromDisk()
    {
        if (_persistencePath == null)
            return;

        if (!File.Exists(_persistencePath))
            return;

        try
        {
            var json = File.ReadAllText(_persistencePath);
            if (string.IsNullOrWhiteSpace(json))
                return;

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                _onWarn?.Invoke(
                    $"HostBridgeDownloadTrackerStore: could not load persistence file '{_persistencePath}' — " +
                    "starting with empty store. Reason: root JSON value is not an array.");
                return;
            }

            var now = DateTime.UtcNow;
            var shouldPersistCleanedSnapshot = false;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                HostBridgeDownloadItemDto? dto;
                try
                {
                    dto = element.Deserialize<HostBridgeDownloadItemDto>(_jsonOptions);
                }
                catch (Exception ex)
                {
                    _onWarn?.Invoke(
                        $"HostBridgeDownloadTrackerStore: skipping persistence entry — could not deserialize: {ex.Message}");
                    continue;
                }

                if (dto == null)
                    continue;
                if (string.IsNullOrWhiteSpace(dto.DownloadId))
                    continue;
                if (!Enum.IsDefined(typeof(HostBridgeDownloadItemStatus), dto.Status))
                {
                    _onWarn?.Invoke(
                        $"HostBridgeDownloadTrackerStore: skipping entry '{dto.DownloadId}' — invalid status '{dto.Status}'.");
                    continue;
                }

                if (!IsTerminalStatus(dto.Status))
                {
                    shouldPersistCleanedSnapshot = true;
                    _onWarn?.Invoke(
                        $"HostBridgeDownloadTrackerStore: dropping non-terminal entry '{dto.DownloadId}' with status '{dto.Status}' — no resumable worker survives process restart.");
                    continue;
                }

                // Skip entries already past retention window — no need to resurrect stale data.
                if (dto.CompletedAt.HasValue &&
                    now - dto.CompletedAt.Value > _completedRetention)
                {
                    shouldPersistCleanedSnapshot = true;
                    continue;
                }

                try
                {
                    var item = _itemFactory(dto);
                    if (item is null || string.IsNullOrWhiteSpace(item.DownloadId))
                    {
                        _onWarn?.Invoke(
                            $"HostBridgeDownloadTrackerStore: skipping entry '{dto.DownloadId}' — itemFactory returned an item with no DownloadId.");
                        continue;
                    }
                    if (!string.Equals(item.DownloadId, dto.DownloadId, StringComparison.Ordinal))
                    {
                        _onWarn?.Invoke(
                            $"HostBridgeDownloadTrackerStore: skipping entry '{dto.DownloadId}' — itemFactory changed DownloadId to '{item.DownloadId}'.");
                        continue;
                    }

                    _items[item.DownloadId] = item;
                }
                catch (Exception ex)
                {
                    _onWarn?.Invoke(
                        $"HostBridgeDownloadTrackerStore: skipping entry '{dto.DownloadId}' — itemFactory threw: {ex.Message}");
                }
            }

            if (shouldPersistCleanedSnapshot)
                PersistToDisk();
        }
        catch (Exception ex)
        {
            // Corrupt / unreadable file — start empty, never throw on load.
            _onWarn?.Invoke(
                $"HostBridgeDownloadTrackerStore: could not load persistence file '{_persistencePath}' — " +
                $"starting with empty store. Reason: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void PersistToDisk()
    {
        if (_persistencePath == null)
            return;

        lock (_persistLock)
        {
            try
            {
                // Snapshot under the persist lock so an older mutation cannot write a stale
                // pre-lock view after a newer mutation has already persisted.
                var dtos = new List<HostBridgeDownloadItemDto>(_items.Count);
                foreach (var kv in _items)
                    dtos.Add(HostBridgeDownloadItemDto.FromItem(kv.Value));

                var dir = Path.GetDirectoryName(_persistencePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var tmpPath = _persistencePath + ".tmp";
                var json    = JsonSerializer.Serialize(dtos, _jsonOptions);
                File.WriteAllText(tmpPath, json);

                // Atomic rename — on same filesystem this is a single metadata operation.
                File.Move(tmpPath, _persistencePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _onWarn?.Invoke(
                    $"HostBridgeDownloadTrackerStore: could not persist to '{_persistencePath}' — " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
