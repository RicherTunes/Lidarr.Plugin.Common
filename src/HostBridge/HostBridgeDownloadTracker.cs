using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
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
    private string _downloadId = string.Empty;
    private Guid _attemptId;
    private long _revision;
    private int _attemptState = (int)HostBridgeDownloadAttemptState.Queued;
    private long _stateChangedAtUtcTicks;
    private int _attemptInitialized;
    private readonly object _attemptInitializationLock = new();
    private int _status = (int)HostBridgeDownloadItemStatus.Queued;
    private long _progressBits;

    // Store-owned CAS synchronization. Internal for deterministic friend-assembly tests,
    // but never exposed through the public item API and never supplied by callers.
    internal object MutationSync { get; } = new();

    // CompletedAt is stored as ticks (long) for atomic reads/writes. DateTime? on x64 is
    // 16 bytes (1 byte HasValue + 7 padding + 8 ticks), and a plain `get/set` is NOT
    // atomic — the retention sweep can observe HasValue=true paired with the previous
    // DateTime's Ticks (or default(DateTime)=0001-01-01), evicting a fresh item as
    // "completed ~21000 days ago". The Interlocked.Read/Exchange pair makes the
    // observation consistent. 0 means "not completed" (DateTime.MinValue.Ticks = 0).
    private long _completedAtTicks;

    public string DownloadId
    {
        get => _downloadId;
        init => _downloadId = value;
    }

    public Guid AttemptId => _attemptId;
    public long Revision => Interlocked.Read(ref _revision);
    public HostBridgeDownloadAttemptState AttemptState =>
        (HostBridgeDownloadAttemptState)Volatile.Read(ref _attemptState);
    public DateTime StateChangedAtUtc =>
        new(Interlocked.Read(ref _stateChangedAtUtcTicks), DateTimeKind.Utc);

    internal void InitializeAttempt(string normalizedId, Guid attemptId, DateTime changedAtUtc)
    {
        lock (_attemptInitializationLock)
        {
            if (_attemptInitialized != 0)
                return;

            InitializeAttemptCore(normalizedId, attemptId, changedAtUtc);
        }
    }

    internal void InitializeAttempt(
        string normalizedId,
        Func<Guid> attemptIdFactory,
        Func<DateTime> changedAtUtcFactory)
    {
        lock (_attemptInitializationLock)
        {
            if (_attemptInitialized != 0)
                return;

            InitializeAttemptCore(normalizedId, attemptIdFactory(), changedAtUtcFactory());
        }
    }

    private void InitializeAttemptCore(string normalizedId, Guid attemptId, DateTime changedAtUtc)
    {
        _downloadId = normalizedId;
        _attemptId = attemptId;
        Volatile.Write(ref _attemptState, (int)HostBridgeDownloadAttemptState.Queued);
        Interlocked.Exchange(ref _stateChangedAtUtcTicks, changedAtUtc.Ticks);
        Interlocked.Exchange(ref _revision, 1);
        Volatile.Write(ref _attemptInitialized, 1);
    }

    internal void RestoreAttempt(
        Guid attemptId,
        long revision,
        HostBridgeDownloadAttemptState attemptState,
        DateTime stateChangedAtUtc)
    {
        _attemptId = attemptId;
        Interlocked.Exchange(ref _revision, revision);
        Volatile.Write(ref _attemptState, (int)attemptState);
        Interlocked.Exchange(ref _stateChangedAtUtcTicks, stateChangedAtUtc.Ticks);
        if (attemptId != Guid.Empty || revision != 0)
            Volatile.Write(ref _attemptInitialized, 1);
    }

    internal HostBridgeQueueMutationKey MutationKey() =>
        new(_downloadId, _attemptId, Revision);

    internal void ApplyTransition(HostBridgeDownloadAttemptState target, DateTime changedAtUtc)
    {
        Volatile.Write(ref _attemptState, (int)target);
        Interlocked.Increment(ref _revision);
        var nextTicks = Math.Max(
            changedAtUtc.Ticks,
            Interlocked.Read(ref _stateChangedAtUtcTicks) + 1);
        Interlocked.Exchange(ref _stateChangedAtUtcTicks, nextTicks);
    }

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

    [JsonPropertyName("attemptId")]
    public Guid AttemptId { get; set; }

    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("attemptState")]
    public HostBridgeDownloadAttemptState AttemptState { get; set; }

    [JsonPropertyName("stateChangedAtUtc")]
    public DateTime StateChangedAtUtc { get; set; }

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
        AttemptId   = item.AttemptId,
        Revision    = item.Revision,
        AttemptState = item.AttemptState,
        StateChangedAtUtc = item.StateChangedAtUtc,
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
        item.RestoreAttempt(AttemptId, Revision, AttemptState, StateChangedAtUtc);
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
    private const int MaxReplaceAttempts = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const long MaxPersistenceBytes = 16L * 1024 * 1024;
    private const int MaxPersistedItems = 10_000;
    private const int MaxPersistedStringLength = 32 * 1024;
    private static readonly TimeSpan MaxWorkerShutdownTimeout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, TItem> _items;
    private readonly TimeSpan _completedRetention;
    private readonly string? _persistencePath;
    private readonly Func<HostBridgeDownloadItemDto, TItem> _itemFactory;
    private readonly Action<string>? _onWarn;
    private readonly HostBridgeQueueStoreOptions _options;
    private readonly string? _ownedStagingRoot;
    private bool _persistenceWriteDisabled;

    // AttemptV2 lock order is membership -> item.MutationSync -> persistence.
    // Persistence warnings are captured under these locks and dispatched only after every
    // acquired lock has been released. Never acquire an earlier lock from a later one.
    private readonly object _membershipLock = new();
    private readonly object _persistLock = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class QueueEnvelope
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = 2;

        [JsonPropertyName("items")]
        public List<HostBridgeDownloadItemDto> Items { get; set; } = new();
    }

    private sealed class BoundedPersistenceStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacityFor(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacityFor(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacityFor(1);
            base.WriteByte(value);
        }

        private void EnsureCapacityFor(int count)
        {
            if (Position > MaxPersistenceBytes - count)
                throw new PersistenceLimitException();
        }
    }

    private sealed class PersistenceLimitException : Exception
    {
    }

    private enum V2SnapshotValidationFailure
    {
        None,
        LimitExceeded,
        InvalidState,
    }

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
    /// <param name="options">
    /// Optional queue contract settings. Defaults to the backward-compatible LegacyV1 contract.
    /// </param>
    public HostBridgeDownloadTrackerStore(
        TimeSpan? completedRetention = null,
        string? persistencePath = null,
        Func<HostBridgeDownloadItemDto, TItem>? itemFactory = null,
        Action<string>? onWarn = null,
        HostBridgeQueueStoreOptions? options = null)
    {
        _completedRetention = completedRetention ?? TimeSpan.FromMinutes(30);
        _persistencePath    = persistencePath;
        _onWarn             = onWarn;
        _itemFactory        = itemFactory ?? DefaultItemFactory;
        _options            = options ?? new HostBridgeQueueStoreOptions();
        _ownedStagingRoot   = SnapshotOwnedStagingRoot(_options.OwnedStagingRoot);
        _items              = new ConcurrentDictionary<string, TItem>(
            _options.ContractVersion == HostBridgeQueueContractVersion.AttemptV2
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

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
        if (_options.ContractVersion == HostBridgeQueueContractVersion.AttemptV2)
        {
            var result = TryAddAttempt(item);
            if (!result.Applied)
                throw new InvalidOperationException(
                    $"Attempt '{item.DownloadId}' was rejected with code {result.Code}.");
            return;
        }

        if (string.IsNullOrWhiteSpace(item.DownloadId))
            throw new ArgumentException("DownloadId must be non-empty.", nameof(item));

        _items[item.DownloadId] = item;
        PersistAndNotify();
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
        if (_options.ContractVersion == HostBridgeQueueContractVersion.AttemptV2)
            return TryAddAttempt(item).Applied;
        if (string.IsNullOrWhiteSpace(item.DownloadId))
            throw new ArgumentException("DownloadId must be non-empty.", nameof(item));

        var added = _items.TryAdd(item.DownloadId, item);
        if (added)
            PersistAndNotify();
        return added;
    }

    /// <summary>
    /// Retrieve a tracker item by DownloadId. Used by the download client's
    /// progress-callback path to update an in-flight item.
    /// </summary>
    public bool TryGet(string downloadId, [NotNullWhen(true)] out TItem? item)
    {
        if (_options.ContractVersion == HostBridgeQueueContractVersion.LegacyV1)
        {
            if (string.IsNullOrWhiteSpace(downloadId))
            {
                item = null;
                return false;
            }

            return _items.TryGetValue(downloadId, out item);
        }

        var normalized = NormalizeDownloadId(downloadId);
        return _items.TryGetValue(normalized, out item);
    }

    public HostBridgeQueueMutationResult<TItem> TryAddAttempt(TItem item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        if (_options.ContractVersion != HostBridgeQueueContractVersion.AttemptV2)
            throw new InvalidOperationException("TryAddAttempt requires AttemptV2 store options.");

        var normalized = NormalizeDownloadId(item.DownloadId);
        HostBridgeQueueMutationResult<TItem> result;
        string? warning = null;

        lock (_membershipLock)
        {
            if (_items.TryGetValue(normalized, out var existing))
            {
                return new(false, HostBridgeQueueResultCodes.Conflict, existing.MutationKey(), existing);
            }

            var candidate = HostBridgeDownloadItemDto.FromItem(item);
            var initialized = candidate.AttemptId != Guid.Empty || candidate.Revision != 0;
            if (!initialized)
            {
                candidate.DownloadId = normalized;
                candidate.AttemptId = Guid.NewGuid();
                candidate.Revision = 1;
                candidate.AttemptState = HostBridgeDownloadAttemptState.Queued;
                candidate.StateChangedAtUtc = EnsureUtc(_options.UtcNow());
            }

            if (!TryValidateProjectedV2Snapshot(
                    candidate,
                    replacedDownloadId: null,
                    out var validationFailure,
                    out _))
            {
                return new(false, ValidationResultCode(validationFailure), default, null);
            }

            if (!initialized)
            {
                item.InitializeAttempt(
                    normalized,
                    candidate.AttemptId,
                    candidate.StateChangedAtUtc);
            }

            _items[normalized] = item;
            warning = PersistToDisk();
            result = new(true, HostBridgeQueueResultCodes.Applied, item.MutationKey(), item);
        }

        NotifyWarning(warning);
        return result;
    }

    internal bool TryRollbackAttempt(HostBridgeQueueMutationKey committed, TItem committedItem)
    {
        if (_options.ContractVersion != HostBridgeQueueContractVersion.AttemptV2)
            throw new InvalidOperationException("TryRollbackAttempt requires AttemptV2 store options.");
        if (committedItem is null) throw new ArgumentNullException(nameof(committedItem));

        var normalized = NormalizeDownloadId(committed.DownloadId);
        string? warning = null;
        var removed = false;
        lock (_membershipLock)
        {
            if (_items.TryGetValue(normalized, out var current) &&
                ReferenceEquals(current, committedItem) &&
                current.MutationKey() == committed)
            {
                removed = _items.TryRemove(normalized, out _);
                if (removed)
                    warning = PersistToDisk();
            }
        }

        NotifyWarning(warning);
        return removed;
    }

    public HostBridgeQueueMutationResult<TItem> TryTransition(
        HostBridgeQueueMutationKey expected,
        HostBridgeDownloadAttemptState target)
    {
        if (_options.ContractVersion != HostBridgeQueueContractVersion.AttemptV2)
            return new(false, HostBridgeQueueResultCodes.IllegalTransition, default, null);

        var normalized = NormalizeDownloadId(expected.DownloadId);
        HostBridgeQueueMutationResult<TItem> result;
        string? warning = null;

        lock (_membershipLock)
        {
            if (!_items.TryGetValue(normalized, out var item))
                return new(false, HostBridgeQueueResultCodes.NotFound, default, null);

            lock (item.MutationSync)
            {
                if (!_items.TryGetValue(normalized, out var attached) ||
                    !ReferenceEquals(attached, item))
                {
                    return attached is null
                        ? new(false, HostBridgeQueueResultCodes.NotFound, default, null)
                        : new(false, HostBridgeQueueResultCodes.Conflict, attached.MutationKey(), attached);
                }

                var current = item.MutationKey();
                if (current.AttemptId != expected.AttemptId || current.Revision != expected.Revision)
                    return new(false, HostBridgeQueueResultCodes.Conflict, current, item);
                if (!IsPersistenceValidV2Dto(HostBridgeDownloadItemDto.FromItem(item)))
                {
                    return new(
                        false,
                        HostBridgeQueueResultCodes.PersistenceInvalidState,
                        current,
                        item);
                }
                if (!HostBridgeQueueStateMachine.CanTransition(item.AttemptState, target))
                    return new(false, HostBridgeQueueResultCodes.IllegalTransition, current, item);
                if (item.Revision == long.MaxValue || item.StateChangedAtUtc.Ticks == DateTime.MaxValue.Ticks)
                    return new(false, HostBridgeQueueResultCodes.MetadataExhausted, current, item);

                var changedAtUtc = EnsureUtc(_options.UtcNow());
                var projected = HostBridgeDownloadItemDto.FromItem(item);
                projected.Revision++;
                projected.AttemptState = target;
                projected.StateChangedAtUtc = new DateTime(
                    Math.Max(changedAtUtc.Ticks, item.StateChangedAtUtc.Ticks + 1),
                    DateTimeKind.Utc);
                if (!TryValidateProjectedV2Snapshot(
                        projected,
                        normalized,
                        out var validationFailure,
                        out _))
                {
                    return new(
                        false,
                        ValidationResultCode(validationFailure),
                        current,
                        item);
                }

                item.ApplyTransition(target, changedAtUtc);
                warning = PersistToDisk();
                result = new(true, HostBridgeQueueResultCodes.Applied, item.MutationKey(), item);
            }
        }

        NotifyWarning(warning);
        return result;
    }

    /// <summary>
    /// Snapshot of currently-tracked items. Side-effect: evicts completed/failed items past
    /// the retention window (FIFO sweep). Plugins call this from
    /// <c>DownloadClientBase.GetItems()</c>.
    /// </summary>
    public IEnumerable<TItem> GetSnapshot()
    {
        if (_options.ContractVersion == HostBridgeQueueContractVersion.LegacyV1)
        {
            var legacy = CollectSnapshot(out var legacyEvicted);
            if (legacyEvicted) PersistAndNotify();
            return legacy;
        }

        List<TItem> result;
        string? warning = null;
        lock (_membershipLock)
        {
            result = CollectSnapshot(out var evicted);
            if (evicted) warning = PersistToDisk();
        }

        NotifyWarning(warning);
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
        var key = _options.ContractVersion == HostBridgeQueueContractVersion.AttemptV2
            ? NormalizeDownloadId(downloadId)
            : downloadId;
        string? warning = null;
        if (_options.ContractVersion == HostBridgeQueueContractVersion.AttemptV2)
        {
            lock (_membershipLock)
            {
                if (!_items.TryRemove(key, out removed))
                    return false;
                warning = PersistToDisk();
            }
            NotifyWarning(warning);
        }
        else
        {
            if (!_items.TryRemove(key, out removed))
                return false;
            PersistAndNotify();
        }

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
    /// Removes one AttemptV2 entry by exact mutation key. Active attempts first persist a
    /// cancelling state and wait for their worker to stop before state or owned files are removed.
    /// </summary>
    public async Task<HostBridgeQueueRemovalResult<TItem>> RemoveAttemptAsync(
        HostBridgeQueueMutationKey expected,
        bool deleteData,
        Func<TItem, CancellationToken, Task> stopWorker,
        TimeSpan shutdownTimeout,
        CancellationToken cancellationToken = default)
    {
        if (_options.ContractVersion != HostBridgeQueueContractVersion.AttemptV2)
            throw new InvalidOperationException("RemoveAttemptAsync requires AttemptV2 store options.");
        if (stopWorker is null) throw new ArgumentNullException(nameof(stopWorker));
        if (shutdownTimeout <= TimeSpan.Zero || shutdownTimeout > MaxWorkerShutdownTimeout)
            throw new ArgumentOutOfRangeException(nameof(shutdownTimeout));

        var normalized = NormalizeDownloadId(expected.DownloadId);
        TItem item;
        HostBridgeQueueMutationKey removalKey;

        lock (_membershipLock)
        {
            if (!_items.TryGetValue(normalized, out item!))
                return new(false, false, false, false, HostBridgeQueueResultCodes.NotFound, null, null);

            var current = item.MutationKey();
            if (current.AttemptId != expected.AttemptId || current.Revision != expected.Revision)
                return new(true, false, false, false, HostBridgeQueueResultCodes.Conflict, null, item);
        }

        if (!HostBridgeQueueStateMachine.IsTerminal(item.AttemptState))
        {
            var cancelling = TryTransition(expected, HostBridgeDownloadAttemptState.Cancelling);
            if (!cancelling.Applied)
            {
                return new(
                    cancelling.Code != HostBridgeQueueResultCodes.NotFound,
                    false,
                    false,
                    false,
                    cancelling.Code,
                    null,
                    cancelling.Item);
            }

            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            shutdown.CancelAfter(shutdownTimeout);
            var workerTask = Task.Run(() => stopWorker(item, shutdown.Token), CancellationToken.None);
            _ = workerTask.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            try
            {
                await workerTask.WaitAsync(shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                shutdown.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return new(
                    true,
                    false,
                    false,
                    false,
                    HostBridgeQueueResultCodes.WorkerShutdownTimeout,
                    null,
                    item);
            }

            var cancelled = TryTransition(cancelling.Current, HostBridgeDownloadAttemptState.Cancelled);
            if (!cancelled.Applied)
            {
                return new(
                    cancelled.Code != HostBridgeQueueResultCodes.NotFound,
                    false,
                    false,
                    false,
                    cancelled.Code,
                    null,
                    cancelled.Item);
            }

            removalKey = cancelled.Current;
        }
        else
        {
            removalKey = expected;
        }

        string? warning = null;
        HostBridgeQueueRemovalResult<TItem> result;
        lock (_membershipLock)
        {
            if (!_items.TryGetValue(normalized, out var attached))
                return new(false, false, false, false, HostBridgeQueueResultCodes.NotFound, null, null);

            lock (attached.MutationSync)
            {
                if (!_items.TryGetValue(normalized, out var currentItem) ||
                    !ReferenceEquals(currentItem, attached))
                {
                    return currentItem is null
                        ? new(false, false, false, false, HostBridgeQueueResultCodes.NotFound, null, null)
                        : new(true, false, false, false, HostBridgeQueueResultCodes.Conflict, null, currentItem);
                }

                var current = attached.MutationKey();
                if (current.AttemptId != removalKey.AttemptId || current.Revision != removalKey.Revision)
                    return new(true, false, false, false, HostBridgeQueueResultCodes.Conflict, null, attached);

                if (deleteData)
                {
                    return new(
                        true,
                        false,
                        false,
                        true,
                        HostBridgeQueueResultCodes.RemovalDeferred,
                        null,
                        attached);
                }

                if (!_items.TryRemove(new KeyValuePair<string, TItem>(normalized, attached)))
                    return new(true, false, false, false, HostBridgeQueueResultCodes.Conflict, null, attached);

                warning = PersistToDisk();
                result = new(
                    true,
                    true,
                    false,
                    false,
                    HostBridgeQueueResultCodes.Removed,
                    null,
                    attached);
            }
        }

        try { NotifyWarning(warning); }
        catch { /* removal is committed; diagnostics observers cannot roll it back */ }
        return result;
    }

    /// <summary>
    /// Persist the current in-memory tracker state immediately. No-op for stores created
    /// without a <c>persistencePath</c>.
    ///
    /// <para>Call this after mutating a tracked item in-place when the mutation happens
    /// outside Common's <see cref="HostBridgeDownloadOrchestrator"/>. Add/remove/eviction
    /// already flush automatically.</para>
    /// </summary>
    public void PersistSnapshot()
    {
        if (_options.ContractVersion == HostBridgeQueueContractVersion.LegacyV1)
        {
            PersistAndNotify();
            return;
        }

        string? warning;
        lock (_membershipLock)
            warning = PersistToDisk();
        NotifyWarning(warning);
    }

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

    private List<TItem> CollectSnapshot(out bool evicted)
    {
        var now = DateTime.UtcNow;
        var result = new List<TItem>(_items.Count);
        evicted = false;

        foreach (var kv in _items)
        {
            var item = kv.Value;
            var status = item.GetStatus();
            if (_options.ContractVersion == HostBridgeQueueContractVersion.LegacyV1 &&
                IsTerminalStatus(status) &&
                item.CompletedAt.HasValue &&
                now - item.CompletedAt.Value > _completedRetention)
            {
                _items.TryRemove(kv.Key, out _);
                evicted = true;
                continue;
            }

            result.Add(item);
        }

        return result;
    }

    private static string NormalizeDownloadId(string downloadId)
    {
        if (string.IsNullOrWhiteSpace(downloadId))
            throw new ArgumentException("DownloadId must be non-empty.", nameof(downloadId));
        return downloadId.Trim();
    }

    private static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

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

    private static string? SnapshotOwnedStagingRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        try { return NormalizeDirectoryPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return path;
        }
    }

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
            if (_options.ContractVersion == HostBridgeQueueContractVersion.AttemptV2 &&
                new FileInfo(_persistencePath).Length > MaxPersistenceBytes)
            {
                DisablePersistenceWrites($"maximum persistence size of {MaxPersistenceBytes} bytes exceeded");
                return;
            }

            var json = File.ReadAllText(_persistencePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                if (_options.ContractVersion == HostBridgeQueueContractVersion.AttemptV2)
                    DisablePersistenceWrites("empty or whitespace persistence content");
                return;
            }

            using var document = JsonDocument.Parse(json);
            if (_options.ContractVersion == HostBridgeQueueContractVersion.LegacyV1)
            {
                LoadLegacySnapshot(document.RootElement);
                return;
            }

            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                if (!ValidateItemCount(document.RootElement))
                    return;
                MigrateV1Snapshot(document.RootElement);
                return;
            }

            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schemaVersion", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number ||
                !versionElement.TryGetInt32(out var schemaVersion))
            {
                DisablePersistenceWrites("missing or non-numeric schemaVersion");
                return;
            }

            if (schemaVersion > (int)HostBridgeQueueContractVersion.AttemptV2)
            {
                DisablePersistenceWrites($"newer schemaVersion {schemaVersion}");
                return;
            }

            if (schemaVersion != (int)HostBridgeQueueContractVersion.AttemptV2 ||
                !document.RootElement.TryGetProperty("items", out var itemsElement) ||
                itemsElement.ValueKind != JsonValueKind.Array)
            {
                DisablePersistenceWrites($"unsupported or malformed schemaVersion {schemaVersion}");
                return;
            }

            if (!ValidateItemCount(itemsElement))
                return;
            LoadV2Snapshot(itemsElement);
        }
        catch (Exception ex)
        {
            // Corrupt / unreadable file — start empty, never throw on load.
            if (_options.ContractVersion == HostBridgeQueueContractVersion.AttemptV2)
                _persistenceWriteDisabled = true;
            _onWarn?.Invoke(
                $"HostBridgeDownloadTrackerStore: could not load persistence file '{_persistencePath}' — " +
                $"starting with empty store. Reason: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void LoadLegacySnapshot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            _onWarn?.Invoke(
                $"HostBridgeDownloadTrackerStore: could not load persistence file '{_persistencePath}' — " +
                "starting with empty store. Reason: root JSON value is not an array.");
            return;
        }

        var now = DateTime.UtcNow;
        var shouldPersistCleanedSnapshot = false;
        foreach (var element in root.EnumerateArray())
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

            if (dto == null || string.IsNullOrWhiteSpace(dto.DownloadId))
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

            if (dto.CompletedAt.HasValue && now - dto.CompletedAt.Value > _completedRetention)
            {
                shouldPersistCleanedSnapshot = true;
                continue;
            }

            TryRestoreLegacyItem(dto);
        }

        if (shouldPersistCleanedSnapshot)
            NotifyWarning(PersistToDisk());
    }

    private void TryRestoreLegacyItem(HostBridgeDownloadItemDto dto)
    {
        try
        {
            var item = _itemFactory(dto);
            if (item is null || string.IsNullOrWhiteSpace(item.DownloadId))
            {
                _onWarn?.Invoke(
                    $"HostBridgeDownloadTrackerStore: skipping entry '{dto.DownloadId}' — itemFactory returned an item with no DownloadId.");
                return;
            }
            if (!string.Equals(item.DownloadId, dto.DownloadId, StringComparison.Ordinal))
            {
                _onWarn?.Invoke(
                    $"HostBridgeDownloadTrackerStore: skipping entry '{dto.DownloadId}' — itemFactory changed DownloadId to '{item.DownloadId}'.");
                return;
            }

            _items[item.DownloadId] = item;
        }
        catch (Exception ex)
        {
            _onWarn?.Invoke(
                $"HostBridgeDownloadTrackerStore: skipping entry '{dto.DownloadId}' — itemFactory threw: {ex.Message}");
        }
    }

    private void MigrateV1Snapshot(JsonElement root)
    {
        var candidates = new List<(HostBridgeDownloadItemDto Dto, string NormalizedId, string CanonicalJson, string Hash)>();
        foreach (var element in root.EnumerateArray())
        {
            if (ContainsOversizedString(element))
            {
                DisablePersistenceWrites($"maximum string length of {MaxPersistedStringLength} characters exceeded");
                return;
            }

            HostBridgeDownloadItemDto? dto;
            try
            {
                dto = element.Deserialize<HostBridgeDownloadItemDto>(_jsonOptions);
            }
            catch (Exception ex)
            {
                DisablePersistenceWrites($"malformed V1 entry: {ex.Message}");
                return;
            }

            if (dto == null || string.IsNullOrWhiteSpace(dto.DownloadId) ||
                !Enum.IsDefined(typeof(HostBridgeDownloadItemStatus), dto.Status) ||
                !Enum.IsDefined(typeof(HostBridgeDownloadAttemptState), dto.AttemptState))
            {
                DisablePersistenceWrites("malformed V1 entry");
                return;
            }

            var normalizedId = NormalizeDownloadId(dto.DownloadId);
            var canonicalJson = CanonicalizeV1Dto(dto);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
            candidates.Add((dto, normalizedId, canonicalJson, hash));
        }

        var migrated = new List<TItem>();
        foreach (var group in candidates.GroupBy(candidate => candidate.NormalizedId, StringComparer.OrdinalIgnoreCase))
        {
            var canonicalId = group.Select(candidate => candidate.NormalizedId)
                .OrderBy(id => id, StringComparer.Ordinal)
                .First();
            var selected = group
                .OrderBy(candidate => V1StatusPrecedence(candidate.Dto.Status))
                .ThenBy(candidate => candidate.Hash, StringComparer.Ordinal)
                .First();
            var dto = selected.Dto;
            dto.DownloadId = canonicalId;
            dto.AttemptId = CreateMigrationAttemptId(canonicalId, selected.CanonicalJson);
            dto.Revision = 1;
            dto.AttemptState = RecoverState(MapV1AttemptState(dto.Status), _options.RestartEvidence(dto));
            dto.StateChangedAtUtc = MigrationStateChangedAtUtc(dto);

            if (!TryCreateV2Item(dto, out var item, out var reason))
            {
                DisablePersistenceWrites($"invalid migrated V1 snapshot: {reason}");
                return;
            }
            migrated.Add(item!);
        }

        foreach (var item in migrated)
            _items[item.DownloadId] = item;
        NotifyWarning(PersistToDisk());
    }

    private void LoadV2Snapshot(JsonElement itemsElement)
    {
        var restored = new List<TItem>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recoveredAny = false;
        foreach (var element in itemsElement.EnumerateArray())
        {
            if (ContainsOversizedString(element))
            {
                DisablePersistenceWrites($"maximum string length of {MaxPersistedStringLength} characters exceeded");
                return;
            }

            HostBridgeDownloadItemDto? dto;
            try
            {
                dto = element.Deserialize<HostBridgeDownloadItemDto>(_jsonOptions);
            }
            catch (Exception ex)
            {
                DisablePersistenceWrites($"malformed V2 entry: {ex.Message}");
                return;
            }

            if (dto == null || string.IsNullOrWhiteSpace(dto.DownloadId) ||
                dto.AttemptId == Guid.Empty || dto.Revision < 1 ||
                !Enum.IsDefined(typeof(HostBridgeDownloadItemStatus), dto.Status) ||
                !Enum.IsDefined(typeof(HostBridgeDownloadAttemptState), dto.AttemptState) ||
                dto.StateChangedAtUtc == default)
            {
                DisablePersistenceWrites("malformed V2 entry metadata");
                return;
            }

            dto.DownloadId = NormalizeDownloadId(dto.DownloadId);
            dto.StateChangedAtUtc = EnsureUtc(dto.StateChangedAtUtc);
            if (!ids.Add(dto.DownloadId))
            {
                DisablePersistenceWrites($"duplicate V2 downloadId '{dto.DownloadId}'");
                return;
            }

            HostBridgeRestartEvidence evidence;
            try
            {
                evidence = _options.RestartEvidence(dto);
            }
            catch (Exception ex)
            {
                DisablePersistenceWrites($"restart evidence failed for '{dto.DownloadId}': {ex.Message}");
                return;
            }

            var recovered = RecoverState(dto.AttemptState, evidence);
            if (recovered != dto.AttemptState)
            {
                if (dto.Revision == long.MaxValue || dto.StateChangedAtUtc.Ticks == DateTime.MaxValue.Ticks)
                {
                    DisablePersistenceWrites($"restart metadata exhausted for '{dto.DownloadId}'");
                    return;
                }
                dto.Revision++;
                dto.AttemptState = recovered;
                var clockTicks = EnsureUtc(_options.UtcNow()).Ticks;
                dto.StateChangedAtUtc = new DateTime(
                    Math.Max(clockTicks, dto.StateChangedAtUtc.Ticks + 1),
                    DateTimeKind.Utc);
                recoveredAny = true;
            }

            if (!TryCreateV2Item(dto, out var item, out var reason))
            {
                DisablePersistenceWrites($"invalid V2 entry: {reason}");
                return;
            }
            restored.Add(item!);
        }

        foreach (var item in restored)
            _items[item.DownloadId] = item;
        if (recoveredAny)
            NotifyWarning(PersistToDisk());
    }

    private bool TryCreateV2Item(HostBridgeDownloadItemDto dto, out TItem? item, out string reason)
    {
        try
        {
            item = _itemFactory(dto);
            if (item is null || !string.Equals(item.DownloadId, dto.DownloadId, StringComparison.Ordinal))
            {
                reason = "itemFactory changed or removed DownloadId";
                return false;
            }
            item.RestoreAttempt(dto.AttemptId, dto.Revision, dto.AttemptState, dto.StateChangedAtUtc);
            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            item = null;
            reason = $"itemFactory threw: {ex.Message}";
            return false;
        }
    }

    private bool ValidateItemCount(JsonElement items)
    {
        if (items.GetArrayLength() <= MaxPersistedItems)
            return true;

        DisablePersistenceWrites($"maximum item count of {MaxPersistedItems} exceeded");
        return false;
    }

    private static bool ContainsOversizedString(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString()!.Length > MaxPersistedStringLength;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (ContainsOversizedString(property.Value))
                        return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    if (ContainsOversizedString(child))
                        return true;
                }
                return false;
            default:
                return false;
        }
    }

    private bool TryValidateProjectedV2Snapshot(
        HostBridgeDownloadItemDto projected,
        string? replacedDownloadId,
        out V2SnapshotValidationFailure failure,
        out string reason)
    {
        var dtos = new List<HostBridgeDownloadItemDto>(_items.Count + 1);
        foreach (var pair in _items)
        {
            if (replacedDownloadId is not null &&
                string.Equals(pair.Key, replacedDownloadId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            dtos.Add(HostBridgeDownloadItemDto.FromItem(pair.Value));
        }
        dtos.Add(projected);
        return TrySerializeV2Snapshot(dtos, out _, out failure, out reason);
    }

    private static bool TrySerializeV2Snapshot(
        List<HostBridgeDownloadItemDto> dtos,
        out byte[] json,
        out V2SnapshotValidationFailure failure,
        out string reason)
    {
        json = Array.Empty<byte>();
        if (dtos.Count > MaxPersistedItems)
        {
            failure = V2SnapshotValidationFailure.LimitExceeded;
            reason = $"maximum item count of {MaxPersistedItems} exceeded";
            return false;
        }

        foreach (var dto in dtos)
        {
            if (!IsPersistenceValidV2Dto(dto))
            {
                failure = V2SnapshotValidationFailure.InvalidState;
                reason = "snapshot contains persistence-invalid item state";
                return false;
            }

            if (PersistedStrings(dto).Any(value => value is not null && value.Length > MaxPersistedStringLength))
            {
                failure = V2SnapshotValidationFailure.LimitExceeded;
                reason = $"maximum string length of {MaxPersistedStringLength} characters exceeded";
                return false;
            }
        }

        dtos.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.DownloadId, right.DownloadId));
        try
        {
            using var stream = new BoundedPersistenceStream();
            JsonSerializer.Serialize(stream, new QueueEnvelope { Items = dtos }, _jsonOptions);
            json = stream.ToArray();
        }
        catch (PersistenceLimitException)
        {
            json = Array.Empty<byte>();
            failure = V2SnapshotValidationFailure.LimitExceeded;
            reason = $"maximum persistence size of {MaxPersistenceBytes} bytes exceeded";
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or NotSupportedException)
        {
            json = Array.Empty<byte>();
            failure = V2SnapshotValidationFailure.InvalidState;
            reason = "snapshot contains a value unsupported by the persistence serializer";
            return false;
        }

        failure = V2SnapshotValidationFailure.None;
        reason = string.Empty;
        return true;
    }

    private static bool IsPersistenceValidV2Dto(HostBridgeDownloadItemDto dto) =>
        !string.IsNullOrWhiteSpace(dto.DownloadId) &&
        dto.AttemptId != Guid.Empty &&
        dto.Revision >= 1 &&
        Enum.IsDefined(typeof(HostBridgeDownloadItemStatus), dto.Status) &&
        Enum.IsDefined(typeof(HostBridgeDownloadAttemptState), dto.AttemptState) &&
        dto.StateChangedAtUtc != default &&
        double.IsFinite(dto.Progress);

    private static string ValidationResultCode(V2SnapshotValidationFailure failure) => failure switch
    {
        V2SnapshotValidationFailure.LimitExceeded => HostBridgeQueueResultCodes.PersistenceLimitExceeded,
        V2SnapshotValidationFailure.InvalidState => HostBridgeQueueResultCodes.PersistenceInvalidState,
        _ => throw new ArgumentOutOfRangeException(nameof(failure)),
    };

    private static IEnumerable<string?> PersistedStrings(HostBridgeDownloadItemDto dto)
    {
        yield return dto.DownloadId;
        yield return dto.AlbumId;
        yield return dto.Title;
        yield return dto.Artist;
        yield return dto.OutputPath;
    }

    private void DisablePersistenceWrites(string reason)
    {
        _persistenceWriteDisabled = true;
        _onWarn?.Invoke(
            $"HostBridgeDownloadTrackerStore: could not load persistence file '{_persistencePath}' — " +
            $"starting with empty store. Reason: {reason}.");
    }

    private static int V1StatusPrecedence(HostBridgeDownloadItemStatus status) => status switch
    {
        HostBridgeDownloadItemStatus.Completed => 0,
        HostBridgeDownloadItemStatus.Failed => 1,
        HostBridgeDownloadItemStatus.Cancelled => 2,
        _ => 3,
    };

    private static HostBridgeDownloadAttemptState MapV1AttemptState(HostBridgeDownloadItemStatus status) => status switch
    {
        HostBridgeDownloadItemStatus.Completed => HostBridgeDownloadAttemptState.CompletedImportable,
        HostBridgeDownloadItemStatus.Failed => HostBridgeDownloadAttemptState.Failed,
        HostBridgeDownloadItemStatus.Cancelled => HostBridgeDownloadAttemptState.Cancelled,
        HostBridgeDownloadItemStatus.Downloading => HostBridgeDownloadAttemptState.Downloading,
        _ => HostBridgeDownloadAttemptState.Queued,
    };

    private static HostBridgeDownloadAttemptState RecoverState(
        HostBridgeDownloadAttemptState state,
        HostBridgeRestartEvidence evidence) => state switch
    {
        HostBridgeDownloadAttemptState.Queued => state,
        HostBridgeDownloadAttemptState.Paused => state,
        HostBridgeDownloadAttemptState.Preparing when evidence.AttemptTemporaryStateContained => HostBridgeDownloadAttemptState.Queued,
        HostBridgeDownloadAttemptState.Preparing => HostBridgeDownloadAttemptState.Failed,
        HostBridgeDownloadAttemptState.Downloading when evidence.VerifiedSegmentsAvailable => HostBridgeDownloadAttemptState.Downloading,
        HostBridgeDownloadAttemptState.Downloading => HostBridgeDownloadAttemptState.Queued,
        HostBridgeDownloadAttemptState.Finalizing when evidence.FinalizedOutputValid => HostBridgeDownloadAttemptState.CompletedImportable,
        HostBridgeDownloadAttemptState.Finalizing => HostBridgeDownloadAttemptState.Queued,
        HostBridgeDownloadAttemptState.Cancelling => HostBridgeDownloadAttemptState.Cancelled,
        HostBridgeDownloadAttemptState.CompletedImportable => state,
        HostBridgeDownloadAttemptState.Failed => state,
        HostBridgeDownloadAttemptState.Cancelled => state,
        _ => HostBridgeDownloadAttemptState.Failed,
    };

    private static Guid CreateMigrationAttemptId(string normalizedId, string canonicalDtoJson)
    {
        var payload = Encoding.UTF8.GetBytes("lpc-queue-v1\0" + normalizedId + "\0" + canonicalDtoJson);
        var hash = SHA256.HashData(payload);
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string CanonicalizeV1Dto(HostBridgeDownloadItemDto dto)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("downloadId", dto.DownloadId);
            writer.WriteString("albumId", dto.AlbumId);
            writer.WriteString("title", dto.Title);
            writer.WriteString("artist", dto.Artist);
            writer.WriteString("outputPath", dto.OutputPath);
            writer.WriteString("startedAt", EnsureUtc(dto.StartedAt));
            if (dto.CompletedAt.HasValue)
                writer.WriteString("completedAt", EnsureUtc(dto.CompletedAt.Value));
            else
                writer.WriteNull("completedAt");
            writer.WriteNumber("totalSize", dto.TotalSize);
            writer.WriteString("status", dto.Status.ToString());
            writer.WriteNumber("progress", dto.Progress);
            writer.WriteString("attemptId", dto.AttemptId);
            writer.WriteNumber("revision", dto.Revision);
            writer.WriteString("attemptState", dto.AttemptState.ToString());
            writer.WriteString("stateChangedAtUtc", EnsureUtc(dto.StateChangedAtUtc));
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static DateTime MigrationStateChangedAtUtc(HostBridgeDownloadItemDto dto)
    {
        var value = dto.CompletedAt ?? (dto.StartedAt == default ? DateTime.UnixEpoch : dto.StartedAt);
        return EnsureUtc(value);
    }

    private void PersistAndNotify() => NotifyWarning(PersistToDisk());

    private void NotifyWarning(string? warning)
    {
        if (warning is not null)
            _onWarn?.Invoke(warning);
    }

    private string? PersistToDisk()
    {
        if (_persistencePath == null || _persistenceWriteDisabled)
            return null;

        lock (_persistLock)
        {
            string? tmpPath = null;
            try
            {
                // Snapshot under the persist lock so an older mutation cannot write a stale
                // pre-lock view after a newer mutation has already persisted.
                var dtos = new List<HostBridgeDownloadItemDto>(_items.Count);
                foreach (var kv in _items)
                    dtos.Add(HostBridgeDownloadItemDto.FromItem(kv.Value));

                byte[]? v2Json = null;
                if (_options.ContractVersion == HostBridgeQueueContractVersion.AttemptV2 &&
                    !TrySerializeV2Snapshot(
                        dtos,
                        out v2Json,
                        out var validationFailure,
                        out var validationReason))
                {
                    return $"HostBridgeDownloadTrackerStore: {ValidationResultCode(validationFailure)} — {validationReason}.";
                }

                var dir = Path.GetDirectoryName(_persistencePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                tmpPath = _persistencePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                if (v2Json is not null)
                    File.WriteAllBytes(tmpPath, v2Json);
                else
                    File.WriteAllText(tmpPath, JsonSerializer.Serialize(dtos, _jsonOptions));

                for (var attempt = 1; attempt <= MaxReplaceAttempts; attempt++)
                {
                    try
                    {
                        // Both paths are in the same directory, so replacing an existing
                        // snapshot remains a single filesystem metadata operation.
                        if (File.Exists(_persistencePath))
                            File.Replace(tmpPath, _persistencePath, destinationBackupFileName: null);
                        else
                            File.Move(tmpPath, _persistencePath);

                        tmpPath = null;
                        return null;
                    }
                    catch (IOException ex) when (IsTransientReplaceFailure(ex) && attempt < MaxReplaceAttempts)
                    {
                        Thread.Sleep(TimeSpan.FromMilliseconds(10 * (1 << (attempt - 1))));
                    }
                    catch (IOException ex) when (IsTransientReplaceFailure(ex))
                    {
                        return $"HostBridgeDownloadTrackerStore: could not persist tracker snapshot after {MaxReplaceAttempts} replace attempts — transient file sharing failure.";
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                return $"HostBridgeDownloadTrackerStore: could not persist tracker snapshot — {ex.GetType().Name}.";
            }
            finally
            {
                if (tmpPath is not null)
                {
                    try { File.Delete(tmpPath); } catch { /* best-effort cleanup */ }
                }
            }
        }
    }

    private static bool IsTransientReplaceFailure(IOException exception)
    {
        var nativeError = exception.HResult & 0xFFFF;
        return nativeError is ErrorSharingViolation or ErrorLockViolation;
    }
}
