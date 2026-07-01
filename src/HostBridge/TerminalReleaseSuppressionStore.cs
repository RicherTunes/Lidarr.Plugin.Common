using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Hosting;
using Lidarr.Plugin.Common.Services.Storage;

namespace Lidarr.Plugin.Common.HostBridge;

/// <summary>
/// Durable, bounded, TTL-based suppression store for provider releases known to be terminally unavailable.
/// Plugins use this to stop re-offering a provider release that can never complete while keeping the host
/// download result truthful.
/// </summary>
public sealed class TerminalReleaseSuppressionStore : ITerminalReleaseSuppressionStore
{
    public const int DefaultMaxEntries = 500;
    public const string DefaultFileName = "terminal-release-suppressions.json";

    public static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(30);

    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromHours(1);

    private readonly JsonFileStore<string, TerminalReleaseSuppressionRecord> _store;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _refreshInterval;
    private volatile HashSet<string> _snapshot;
    private DateTimeOffset _lastRefreshUtc;

    public TerminalReleaseSuppressionStore(
        string filePath,
        string serviceName,
        TimeSpan? ttl = null,
        int? maxEntries = null,
        TimeProvider? clock = null,
        TimeSpan? refreshInterval = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path must be supplied", nameof(filePath));
        }

        ServiceName = string.IsNullOrWhiteSpace(serviceName) ? "unknown" : serviceName.Trim();
        _clock = clock ?? TimeProvider.System;
        _refreshInterval = refreshInterval ?? DefaultRefreshInterval;
        _store = new JsonFileStore<string, TerminalReleaseSuppressionRecord>(
            filePath,
            new JsonFileStoreOptions<string>
            {
                Ttl = ttl ?? DefaultTtl,
                MaxEntries = maxEntries ?? DefaultMaxEntries,
                KeyNormalizer = NormalizeReleaseId,
                KeyComparer = StringComparer.OrdinalIgnoreCase,
                Clock = _clock,
            });

        _snapshot = BuildSnapshot();
        _lastRefreshUtc = _clock.GetUtcNow();
    }

    public string ServiceName { get; }

    public int Count => _store.Count;

    public static TerminalReleaseSuppressionStore ForPlugin(
        string pluginName,
        string fileName = DefaultFileName,
        TimeSpan? ttl = null,
        int? maxEntries = null,
        TimeProvider? clock = null,
        TimeSpan? refreshInterval = null)
    {
        if (string.IsNullOrWhiteSpace(pluginName))
        {
            throw new ArgumentException("Plugin name must be supplied", nameof(pluginName));
        }

        var configRoot = PluginConfigRoots.Resolve(pluginName);
        return new TerminalReleaseSuppressionStore(
            Path.Combine(configRoot, fileName),
            pluginName,
            ttl,
            maxEntries,
            clock,
            refreshInterval);
    }

    public bool IsSuppressed(string releaseId)
    {
        if (string.IsNullOrWhiteSpace(releaseId))
        {
            return false;
        }

        MaybeRefresh();
        return _snapshot.Contains(NormalizeReleaseId(releaseId));
    }

    public async Task SuppressAsync(
        string releaseId,
        string? sourceItemId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(releaseId))
        {
            return;
        }

        await _store.SetAsync(
            releaseId,
            new TerminalReleaseSuppressionRecord
            {
                ServiceName = ServiceName,
                ReleaseId = releaseId.Trim(),
                SourceItemId = string.IsNullOrWhiteSpace(sourceItemId) ? null : sourceItemId.Trim(),
                Reason = string.IsNullOrWhiteSpace(reason) ? "Unknown" : reason.Trim(),
                SuppressedAtUtc = _clock.GetUtcNow(),
            },
            cancellationToken).ConfigureAwait(false);

        _snapshot = await BuildSnapshotAsync(cancellationToken).ConfigureAwait(false);
        _lastRefreshUtc = _clock.GetUtcNow();
    }

    public Task SuppressAsync(string releaseId, string? reason, CancellationToken cancellationToken = default)
        => SuppressAsync(releaseId, sourceItemId: null, reason, cancellationToken);

    public Task<bool> ClearAsync(string releaseId, CancellationToken cancellationToken = default)
        => string.IsNullOrWhiteSpace(releaseId)
            ? Task.FromResult(false)
            : ClearCoreAsync(releaseId, cancellationToken);

    private async Task<bool> ClearCoreAsync(string releaseId, CancellationToken cancellationToken)
    {
        var removed = await _store.RemoveAsync(releaseId, cancellationToken).ConfigureAwait(false);
        if (removed)
        {
            _snapshot = await BuildSnapshotAsync(cancellationToken).ConfigureAwait(false);
            _lastRefreshUtc = _clock.GetUtcNow();
        }

        return removed;
    }

    private void MaybeRefresh()
    {
        if (_clock.GetUtcNow() - _lastRefreshUtc < _refreshInterval)
        {
            return;
        }

        _snapshot = BuildSnapshot();
        _lastRefreshUtc = _clock.GetUtcNow();
    }

    private HashSet<string> BuildSnapshot()
        => BuildSnapshotAsync(CancellationToken.None).GetAwaiter().GetResult();

    private async Task<HashSet<string>> BuildSnapshotAsync(CancellationToken cancellationToken)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var pair in _store.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            set.Add(NormalizeReleaseId(pair.Key));
        }

        return set;
    }

    private static string NormalizeReleaseId(string releaseId)
        => releaseId.Trim().ToLowerInvariant();
}

public interface ITerminalReleaseSuppressionStore
{
    bool IsSuppressed(string releaseId);

    Task SuppressAsync(string releaseId, string? sourceItemId, string? reason, CancellationToken cancellationToken = default);

    Task SuppressAsync(string releaseId, string? reason, CancellationToken cancellationToken = default);

    Task<bool> ClearAsync(string releaseId, CancellationToken cancellationToken = default);
}

public sealed class TerminalReleaseSuppressionRecord
{
    public string ServiceName { get; set; } = string.Empty;

    public string ReleaseId { get; set; } = string.Empty;

    public string? SourceItemId { get; set; }

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset SuppressedAtUtc { get; set; }
}

public sealed class NullTerminalReleaseSuppressionStore : ITerminalReleaseSuppressionStore
{
    public static NullTerminalReleaseSuppressionStore Instance { get; } = new();

    private NullTerminalReleaseSuppressionStore()
    {
    }

    public bool IsSuppressed(string releaseId) => false;

    public Task SuppressAsync(string releaseId, string? sourceItemId, string? reason, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SuppressAsync(string releaseId, string? reason, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<bool> ClearAsync(string releaseId, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
