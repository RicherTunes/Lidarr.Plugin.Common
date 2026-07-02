# Lidarr.Plugin.Common.HostBridge

Helpers for the host-side bridge plugins (`brainarr` ImportList, `qobuzarr`/`tidalarr`/`applemusicarr` Indexer+DownloadClient). Each helper was lifted out of identical implementations duplicated across the plugin family in the May 2026 unification pass.

## When to use

You're writing a class that extends Lidarr's `HttpIndexerBase<TSettings>`, `DownloadClientBase<TSettings>`, or `ImportListBase<TSettings>`. You're tempted to write inline:

- `Path.Combine(Settings.DownloadPath, sanitize(artist), sanitize(album))` → use `PathTraversalGuard`
- `release.Guid.Split(':')` parsing → use `PrefixedReleaseGuidParser`
- `static ConcurrentDictionary<string, MyDownloadItem>` for in-flight tracking → use `HostBridgeDownloadTrackerStore<T>`
- `applemusic://search?query=...` placeholder URI roundtrip → use `PlaceholderSearchUri`
- Settings snapshot code before a fire-and-forget download → use `SettingsSnapshot.Copy` or the `HostBridgeDownloadOrchestrator` overload without an explicit snapshotter

If you write one of these inline, you're forking the algorithm — when the next bug or hardening fix lands, your copy doesn't get it.

## Settings snapshots

For settings made of primitive, enum, nullable, and string properties, prefer the orchestrator overload that omits the explicit snapshotter:

```csharp
await _orchestrator.StartTrackedDownloadAsync(
    (MySettings)Definition.Settings,
    _tracker,
    CreateItem,
    ExecuteAsync);
```

If you need the explicit snapshotter overload, pass `SettingsSnapshot.Copy<T>` directly instead of snapshotting into a local first:

```csharp
await _orchestrator.StartTrackedDownloadAsync(
    (MySettings)Definition.Settings,
    _tracker,
    SettingsSnapshot.Copy<MySettings>,
    CreateItem,
    ExecuteAsync);
```

Both routes snapshot public read-write non-indexer properties before `Task.Run`, so the background download sees the original values even if the host mutates live settings afterward. If settings contain mutable reference types such as lists, dictionaries, caches, or credential containers, keep using the explicit `snapshotter` overload and deep-copy those fields yourself.

## Canonical adoption pattern

```csharp
public sealed class MyDownloadClient : DownloadClientBase<MySettings>
{
    // ONE store per process. ForPlugin() persists terminal queue state under the
    // plugin config directory so completed/failed/cancelled items survive a
    // Lidarr restart long enough for host import, blocklist, or removal.
    // Queued/downloading entries remain process-local until a plugin implements
    // a real resumable worker seam.
    private static readonly HostBridgeDownloadTrackerStore<HostBridgeDownloadItem> _tracker =
        HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>.ForPlugin("MyPlugin");

    public override Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer)
    {
        // If you are not using HostBridgeDownloadOrchestrator, snapshot exactly once before Task.Run.
        var snapshot = SettingsSnapshot.Copy((MySettings)Definition.Settings);

        // Extract IDs using the prefix shared with your indexer.
        var albumId = PrefixedReleaseGuidParser.ExtractAlbumId(
            remoteAlbum.Release.Guid, remoteAlbum.Release.InfoUrl, "myservice");
        if (string.IsNullOrWhiteSpace(albumId))
            throw new InvalidOperationException("...");

        // Build + validate the output path.
        var output = Path.Combine(snapshot.DownloadPath,
            PathTraversalGuard.SanitizeSegment(remoteAlbum.Artist?.Name ?? "Unknown Artist"),
            PathTraversalGuard.SanitizeSegment(remoteAlbum.Albums[0].Title ?? "Unknown Album"));
        if (!PathTraversalGuard.IsPathWithinRoot(output, snapshot.DownloadPath))
            throw new InvalidOperationException($"Refusing to write outside DownloadPath: {output}");

        // Track + kick off.
        var item = new HostBridgeDownloadItem
        {
            DownloadId = Guid.NewGuid().ToString("N"),
            AlbumId = albumId,
            Title = remoteAlbum.Albums[0].Title,
            Artist = remoteAlbum.Artist?.Name ?? "Unknown",
            OutputPath = output,
        };
        item.SetStatus(HostBridgeDownloadItemStatus.Downloading);
        _tracker.AddOrReplace(item);

        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteAsync(item, snapshot).ConfigureAwait(false);
            }
            finally
            {
                _tracker.PersistSnapshot();
            }
        });
        return Task.FromResult(item.DownloadId);
    }

    public override IEnumerable<DownloadClientItem> GetItems() =>
        _tracker.GetSnapshot().Select(MapToHostItem);

    public override void RemoveItem(DownloadClientItem hostItem, bool deleteData) =>
        _tracker.Remove(hostItem.DownloadId, deleteData, out _,
            onDeleteError: ex => _logger.Warn(ex, "Failed to delete download data"));

    protected override void Test(List<ValidationFailure> failures)
    {
        new Lidarr.Plugin.Common.Validation.TestValidationBuilder()
            .RequireNonEmpty(nameof(Settings.ApiKey), Settings.ApiKey, "API key is required.")
            .RequireNonEmpty(nameof(Settings.DownloadPath), Settings.DownloadPath, "Download path is required.")
            .RequireIf(!Settings.ProbeOnly, nameof(Settings.UserToken), Settings.UserToken,
                       "User token is required when ProbeOnly is unchecked.")
            .ApplyTo(failures);
        if (failures.Count > 0) return;

        // ... smoke-probe call against your service ...
    }
}

public sealed class MyIndexer : HttpIndexerBase<MySettings>
{
    public override IIndexerRequestGenerator GetRequestGenerator() =>
        new MyRequestGenerator(); // builds PlaceholderSearchUri.Build("myservice", query)

    protected override async Task<IList<ReleaseInfo>> FetchReleases(
        Func<IIndexerRequestGenerator, IndexerPageableRequestChain> selector, bool isRecent = false)
    {
        var releases = new List<ReleaseInfo>();
        foreach (var req in selector(GetRequestGenerator()).GetAllTiers().SelectMany(t => t))
        {
            if (!PlaceholderSearchUri.TryExtractQuery(req.HttpRequest?.Url?.ToString() ?? "",
                                                      "myservice", out var query))
                continue;
            // ... call your service, build ReleaseInfo, add to releases ...
        }
        return CleanupReleases(releases);
    }
}
```

## What's in this folder

| Type | What | Lift origin |
|---|---|---|
| `PathTraversalGuard` | `SanitizeSegment` + `IsPathWithinRoot` for defense-in-depth path containment | apple `AppleMusicLidarrDownloadClient` (May 2026) |
| `PrefixedReleaseGuidParser` | `{indexerId}_{scheme}:album:{id}[:extra]` GUID + InfoUrl path extraction | apple + tidalarr (identical algorithm modulo scheme literal) |
| `HostBridgeDownloadItem` + `HostBridgeDownloadTrackerStore<T>` | Thread-safe per-download tracker + optional write-through JSON persistence for terminal items with retention sweep | apple + tidalarr (byte-for-byte same pattern) |
| `HostBridgeDownloadItemStatus` | Status enum for the tracker (Queued/Downloading/Completed/Failed/Cancelled) | new |
| `PlaceholderSearchUri` | `{scheme}://search?query={encoded}` roundtrip | apple + tidalarr |

`HostBridgeDownloadTrackerStore<T>.ForPlugin("PluginName")` is the canonical adoption path for plugins that use `HostBridgeDownloadItem` directly. Common's `HostBridgeDownloadOrchestrator` flushes the final in-place item mutations after `doWork` exits. If a plugin mutates tracked items outside that orchestrator path, call `_tracker.PersistSnapshot()` after status/progress/completion changes that must survive restart.

Persistence is intentionally terminal-state-only. Completed, failed, and cancelled entries are reloaded until retention expiry so the Lidarr host can import, blocklist, or remove them. Queued and downloading entries are dropped during startup and the cleaned snapshot is written back, because the background worker task does not survive process restart. A future resumable-download feature needs an explicit plugin worker seam instead of pretending stale in-progress JSON can finish itself.

`Remove(downloadId, deleteData: true, ...)` is cross-attempt aware: if another queued/downloading item still targets the same canonical output directory, Common removes only the tracker entry and leaves the directory in place for the active attempt. The comparison normalizes trailing separators and `.`/`..`; it is case-sensitive on Linux and case-insensitive on Windows/macOS to match Common's path-guard semantics.

Subclass stores need an `itemFactory` so persisted base fields can be restored into the intended runtime type. The Common DTO does not persist arbitrary subclass-only fields; derive those fields from base data in the factory, or keep restart-critical state on `HostBridgeDownloadItem`.

Related helpers in sibling namespaces:

- `Lidarr.Plugin.Common.Validation.TestValidationBuilder` — accumulate-then-return required-field validator for `Test()` overrides. Use `.ApplyTo(failures)` to merge into the host's list.
- `Lidarr.Plugin.Common.TestKit.Fakes.NullUniversalAdaptiveRateLimiter` — null-object impl of `IUniversalAdaptiveRateLimiter` for plugin tests that don't need rate-limiting behavior.

## Threat-model notes

`PathTraversalGuard.IsPathWithinRoot` performs LEXICAL canonicalization (`Path.GetFullPath`). It does NOT resolve symlinks (Linux) / junctions (Windows) / NTFS reparse points. The guard is sufficient when the threat model is "metadata source can inject only string segments." If your threat model includes a writable-inside-root attacker, add explicit symlink resolution at the call site (`DirectoryInfo.ResolveLinkTarget(returnFinalTarget: true)`).
