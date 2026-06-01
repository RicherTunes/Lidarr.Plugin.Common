# HostBridge Subsystem

Helpers for the host-side bridge plugins (`brainarr` ImportList, `qobuzarr`/`tidalarr`/`applemusicarr` Indexer + DownloadClient). Each helper was lifted out of identical implementations duplicated across the plugin family in the May 2026 unification pass.

Full source-adjacent documentation: [`src/HostBridge/README.md`](../src/HostBridge/README.md).

## Core types

| Type | Purpose |
|------|---------|
| `HostBridgeDownloadOrchestrator` | Centralizes fire-and-forget download enqueue pattern for streaming plugins. Snapshots settings before enqueue to avoid mutation races. |
| `HostBridgeRuntimeCache<TRuntime, TSettings>` | Generic singleton-runtime cache for Lidarr-native bridge plugins. Stores per-plugin runtime state keyed by settings instance. |
| `HostBridgeDownloadTrackerStore<TItem>` | Thread-safe `ConcurrentDictionary`-backed download tracker with retention sweep. Use one static instance per process. |
| `AlbumSizeEstimator` | Estimates on-disk byte size of a streaming album/track from duration and bitrate. Formula: `bytes = durationSeconds × (bitrate ÷ 8)` with a 3-rung fallback ladder (album duration → summed tracks → count × average). |
| `MultiQualityReleaseBuilder` | Builds multi-quality release variants — one `ReleaseInfo` per quality tier so the host can pick the best match. |
| `AlbumReleaseInfoBuilder` | Builds the three `ReleaseInfo` string fields (`Guid`, `DownloadUrl`, `Title`) for streaming-service plugins. Supports edition, explicit, and live marker brackets. |
| `AlbumDownloadUri` | Build and parse `{scheme}://album/{id}[?quality={q}]` placeholder URIs for streaming download round-trip. |

## Path safety

| Type | Purpose |
|------|---------|
| `PathTraversalGuard` | `SanitizeSegment` + `IsPathWithinRoot` for defense-in-depth path containment. Performs lexical canonicalization only — does not resolve symlinks/junctions. |

## URI helpers

| Type | Purpose |
|------|---------|
| `PlaceholderSearchUri` | Build and parse `{scheme}://search?query={encoded}` placeholder URIs for indexer search round-trip. |
| `PrefixedReleaseGuidParser` | Parse album IDs from `ReleaseInfo.Guid`/`InfoUrl` shapes shared by streaming plugins. |

## Quick start

See the canonical adoption pattern in [`src/HostBridge/README.md`](../src/HostBridge/README.md) — a complete `DownloadClientBase<T>` + `HttpIndexerBase<T>` example using the tracker, path guard, and placeholder URI helpers.

## Related docs

- [Key Services](reference/KEY_SERVICES.md)
- [Plugin Bridge](PLUGIN_BRIDGE.md)
- [Changelog — HostBridge lift wave](../CHANGELOG.md)
