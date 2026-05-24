# Consuming Common Helpers

A cross-plugin guide to the public APIs in Lidarr.Plugin.Common v1.10.0+.

---

## Resilience

### BackendHealthCache

**Problem solved**: connection-refused storms. After 3 consecutive connection-class failures to the same host, all further calls short-circuit immediately for `DefaultGraceSeconds` (120 s) instead of hammering a permanently-down backend and burning Lidarr's 30-second per-request timeout.

**Adopted by**:
- **Brainarr** — `BrainarrOllamaProvider` and `BrainarrLmStudioProvider` hold a private `BackendHealthCache` field; `ModelDetectionService` consults it.
- **Qobuzarr** — `QobuzHttpClient.ExecuteAsync` (direct field at line 31).
- **Tidalarr** — `TidalBackendHealthHandler` `DelegatingHandler` wrapping `BackendHealthCache.Shared`.
- **AppleMusicarr** — `AppleBackendHealthHandler` `DelegatingHandler` (same pattern as Tidalarr).

**Pattern**: wrap the shared singleton in a `DelegatingHandler` or consult `BackendHealthCache.IsConnectionClassFailure(ex)` in your `catch` block; see `tests/Resilience/BackendHealthCacheTests.cs`.

### AuthFailureGate

**Problem solved**: 401/403 cascade gate. After the first auth failure, every subsequent request short-circuits until a probe-interval window elapses, preventing credential-storm hammering.

**Adopted by**:
- **Qobuzarr** — `QobuzarrStreamingPlugin` registers the singleton at startup; `BridgeQobuzApiClient` consumes it.
- **AppleMusicarr** — `AppleMusicarrStreamingPlugin` registers it (line 89); `AppleMusicIndexerAdapter` and `HttpClientFactory` receive it as an optional parameter.

**Pattern**: `services.AddSingleton<AuthFailureGate>()` in your module's `ConfigureServices`; inject into HTTP-layer or indexer adapter. See `src/Services/Bridge/AuthFailureGate.cs`.

---

## Errors

### HttpExceptionClassifier

**Problem solved**: turns a raw `HttpRequestException` or `WebException` into a categorical `HttpFailureCategory` (Auth / RateLimit / Network / Unknown) so retry and gate logic doesn't pattern-match on error strings.

**Adopted by**:
- **Qobuzarr** — `AdaptiveQobuzApiClient` (lines 54, 84) and `AuthTokenManager.IsAuthenticationError` (line 376).

**Pattern**: `var (category, hint) = HttpExceptionClassifier.Classify(ex);` — see `tests/HttpExceptionClassifierTests.cs`.

---

## Observability

### PluginLogContext

**Problem solved**: `AsyncLocal` scopes that stamp every log line with `[Operation:correlationId:provider]` without threading a logger through every call frame.

**Adopted by**:
- **Qobuzarr** — `QobuzIndexer.FetchReleases` (Search scope, line 189), `QobuzIndexer.TestQuery` (Test scope, line 265), `AuthTokenManager` (AuthRefresh scope, line 266).

**Pattern**: `using var _scope = PluginLogContext.Push("MyPlugin", "Search", provider: "myservice");` then prefix log lines with `PluginLogContext.Current?.LinePrefix()`.

### Scrub

**Problem solved**: strips secrets and full URLs from log strings so credentials and stream URLs never land in NLog output.

**Adopted by**:
- **Qobuzarr** — `AudioFileDownloader` (`Scrub.Url`, lines 73/169), `QobuzDownloadClient` (`Scrub.Url`, line 663), `QobuzRequestSigner` (`Scrub.Secret`, line 64).

**Pattern**: `Scrub.Url(rawUrl)` / `Scrub.Secret(rawKey)` inline in log arguments.

### WarnOnce

**Problem solved**: emits a `WARN`-level message the first time a key is seen, then downgrades subsequent fires to `DEBUG` — prevents log spam for recoverable one-time misconfigurations.

**Adopted by**:
- **Brainarr** — `ITokenizer` (tokenizer fallback gate, `Services/Tokenization/ITokenizer.cs:42`).
- **Qobuzarr** — `QobuzIndexer` (wire-warn gate, `src/Indexers/QobuzIndexer.cs:58`).

**Pattern**: `private static readonly WarnOnce _warn = new();` then `if (_warn.ShouldWarn(key)) logger.Warn(...)`.

---

## Hosting

### PluginConfigRoots

**Problem solved**: Docker-safe config directory resolution. Returns `/config/<PluginName>` inside Docker (where `$HOME` is empty), `$HOME/.config/<PluginName>` on bare-metal, or any value set via the `LPC_CONFIG_ROOT` env-var override.

**Adopted by**: all four plugins — Brainarr, Qobuzarr, Tidalarr, AppleMusicarr.

**Pattern**: `PluginConfigRoots.Resolve("MyPlugin")` as the default value for a `ConfigPath` settings property; the parity check `Check_UsesCommonPluginConfigRoots` enforces this in each plugin's `EcosystemParityTests`.

### PluginLifecycle

**Problem solved**: static-disposable teardown registry — registers named `Action` callbacks that run in LIFO order when the plugin module's `Dispose` is called, cleaning up static singletons that can't be injected.

**Adopted by**:
- **Brainarr** — `BrainarrModule` registers `MetricsCollector.Dispose` and `LimiterRegistry.Dispose` (lines 65–66), then calls `PluginLifecycle.Shutdown()` on dispose (line 78).

**Pattern**: `PluginLifecycle.RegisterShutdown("MyHook", MyStatic.Dispose);` in module init; `PluginLifecycle.Shutdown()` in module dispose.

---

## Storage / Collections

### JsonFileStore&lt;TKey, TValue&gt;

**Problem solved**: JSON-backed, mutex-safe key-value store with optional TTL and LRU eviction; replaces hand-rolled `lock`/`File.ReadAllText`/`File.WriteAllText` patterns.

**Adopted by**:
- **Brainarr** — `ReviewQueueService` (review items), `ReviewActionAuditService`.
- **AppleMusicarr** — `FileUnresolvedQueueStore`, `FileMusicBrainzMappingCache` (UPC + ISRC), `FileApplePinnedMappingStore`.

**Pattern**: `new JsonFileStore<string, MyEntry>(Path.Combine(PluginConfigRoots.Resolve("MyPlugin"), "store.json"), new JsonFileStoreOptions<string> { Ttl = TimeSpan.FromDays(30) })`.

---

## Host-Bridge Primitives

### HostBridgeDownloadTrackerStore / HostBridgeDownloadOrchestrator

**Problem solved**: Lidarr calls `GetItems` and `RemoveItem` on its download-client adapter from multiple threads while a background Task runs the actual download. The tracker provides the thread-safe item registry; the orchestrator snapshots settings at enqueue-time and fires the background work.

**Adopted by**:
- **Tidalarr** — `TidalLidarrDownloadClient` (static fields, lines 38–39); `Enqueue` call at line 143.

**Pattern**: `private static readonly HostBridgeDownloadTrackerStore<HostBridgeDownloadItem> ActiveDownloads = new(); private static readonly HostBridgeDownloadOrchestrator _orchestrator = new(logger: null);`.

### PrefixedReleaseGuidParser

**Problem solved**: parses the colon-grammar GUID `scheme:album:<id>` that Common and plugin indexers emit, extracting the raw album ID for download-client lookup.

**Adopted by**:
- **Qobuzarr** — `QobuzParser.cs:233`.
- **Tidalarr** — `TidalLidarrDownloadClient.cs:363` (`ExtractAlbumId`).

### PlaceholderSearchUri

**Problem solved**: builds and decodes `scheme://search?query=...` placeholder URLs that Lidarr passes from the indexer to the download client as the "download URL".

**Adopted by**:
- **Tidalarr** — `TidalLidarrIndexer.cs:438` (Build), `TidalLidarrIndexer.cs:139,464` (TryExtractQuery).

### PathTraversalGuard

**Problem solved**: collapses `..` segments and verifies the resulting path is still under the declared root, preventing path-traversal attacks in user-supplied artist/album names.

**Adopted by**:
- **Tidalarr** — `TidalLidarrDownloadClient.cs:401` (`SanitizeSegment` for artist/album path fragments), line 409 (`IsPathWithinRoot`).

### AlbumReleaseInfoBuilder

**Problem solved**: fluent string builder that assembles the GUID, download URL, and human-readable title triple that a host-bridge indexer must return, keeping the colon-grammar in one place.

**Adopted by**:
- **Tidalarr** — `TidalLidarrIndexer.cs:540`, `TidalLidarrIndexer.cs:583`.

### TestValidationBuilder

**Problem solved**: multi-field accumulator for `TestResult` — lets a download-client or indexer collect all validation errors before returning instead of short-circuiting on the first failure.

**Adopted by**:
- **Tidalarr** — `TidalLidarrDownloadClient.cs:307`.
