# RicherTunes Lidarr Plugin Ecosystem — Parity Matrix

<!-- docval:ignore-script-refs: references plugin-side / proposed scripts, not Common tooling -->

**Generated**: 2026-05-25 (Wave 21 parity-mission session). **Refreshed**: 2026-05-25 (Wave-22 adversarial-review pass). **Refreshed again**: 2026-05-25 (Wave-23 adversarial-review pass — apple-drift correction + security hardening + parity convergence). **Refreshed again**: 2026-05-26 (Mission #52 stale-cell pass + Mission #53 rows 31-35 cross-cutting concerns).
> **Scope note (2026-06-29):** This matrix is a historical four-plugin evidence set. The current active ecosystem also includes `amazonmusicarr`; do not treat missing Amazon cells as N/A. Current CI repo coverage is enforced by `scripts/ci/ecosystem-repos.json` + `scripts/ci/verify-ecosystem-ci-contract.ps1`.

**Current Common dev version**: `1.18.0-dev`. **Current five-plugin sentinel pin**: `3c34a5ade2ff99b81f78462474b06e1df49afc14` (verified 2026-06-29 from `ext-common-sha.txt` in amazonmusicarr, applemusicarr, brainarr, qobuzarr, and tidalarr).
**Plugins covered by the historical matrix below**: `applemusicarr`, `tidalarr`, `qobuzarr`, `brainarr` + the shared `lidarr.plugin.common`

This document is the single source of truth for "does every plugin follow the same canonical pattern, or does the divergence have a documented architectural reason?" Each row is a cross-cutting concern; each column is a plugin; each cell is a status + evidence pointer.

**Status legend**:
- ✓ — adopted, canonical pattern in use
- ⚠ — partial / known limitation documented inline
- ✗ — missing / open tech debt
- N/A — architecturally inapplicable; reason documented inline

---

## 1. Plugin lifecycle / discovery axes

| # | Axis | applemusicarr | tidalarr | qobuzarr | brainarr |
|---|------|:-:|:-:|:-:|:-:|
| 1 | Plugin registration (host `NzbDrone.Core.Plugins.Plugin`) | ✓ `AppleMusicarrInstalledPlugin.cs:31-36` | ✓ `TidalarrInstalledPlugin.cs:28-33` | ✓ `QobuzarrInstalledPlugin.cs:29-34` | ✓ `BrainarrInstalledPlugin.cs:30-35` |
| 2 | Assembly name = `Lidarr.Plugin.{Plugin}` | ✓ `AppleMusicarr.Plugin.csproj:11` | ✓ `Tidalarr.csproj:4` | ✓ `Qobuzarr.csproj:8` | ✓ `Brainarr.Plugin.csproj:10` |
| 3 | Release asset filename contains `net8.0.zip` | ✓ `release.yml` + `scripts/pack-plugin.ps1` | ✓ `release.yml:164` | ✓ `release.yml:155` | ✓ `release.yml` (3× `net8.0` literals) |
| 4 | `PluginLifecycle.Shutdown` + `RegisterShutdown` | ✓ `AppleMusicarrStreamingPlugin.cs:56-78` | ✓ `TidalModule.cs:284-313` | ⚠ uses direct `module.Dispose()` instead of `PluginLifecycle.RegisterShutdown` — semantically equivalent, called via `HostGateRegistry.Shutdown` per CHANGELOG v0.5.4 | ✓ `BrainarrModule.cs:65,78` |

**Qobuz divergence (#4)**: `QobuzarrStreamingModule.Dispose` directly tears down its `HostGateRegistry` rather than registering a shutdown delegate with Common's `PluginLifecycle`. Functionally equivalent — both run before assembly unload. Convergence is mechanical (register delegate, call `PluginLifecycle.Shutdown()`) but no user-visible difference; deferred.

---

## 2. Constants / configuration shape

| # | Axis | applemusicarr | tidalarr | qobuzarr | brainarr |
|---|------|:-:|:-:|:-:|:-:|
| 5 | Constants file w/ `PluginName` + `ServiceName` + `PluginVendor` | ✓ `AppleMusicarrConstants.cs:13-35` | ✓ **Wave-22**: `TidalConstants.cs` gains canonical `PluginName`/`ServiceName`/`PluginVendor` const block (commit `6542287`) | ✓ `QobuzarrConstants.cs:10-15` | ✓ **Wave-23**: `BrainarrConstants.cs:7-13` adds the triple (commit `88ad013`). InstalledPlugin still uses literals (load-bearing host registration) but the named-block source of truth now exists. |

---

## 3. Versioning / packaging

| # | Axis | applemusicarr | tidalarr | qobuzarr | brainarr |
|---|------|:-:|:-:|:-:|:-:|
| 6 | VERSION file → Directory.Build.props → plugin.json → assembly | ✓ contract-tested | ✓ `VersionContractTests.cs:43-55` | ✓ `VersionContractTests.cs:38-50` | ✓ `VersionContractTests.cs:56-68` |
| 7 | `commonVersion` coherence (plugin.json ↔ manifest.json ↔ submodule tag) | ✓ `ManifestJson_MatchesPluginJsonCommonVersion` test enforces | ⚠ no explicit `ManifestJson_MatchesPluginJsonCommonVersion` test, but plugin.json autogen prevents drift; manifest.json absent from production builds | ⚠ no explicit three-way `commonVersion` test; plugin.json autogen prevents drift | **Wave 21**: ✓ `ManifestJson_MatchesPluginJsonCommonVersion` test added (commit `441d655`) — closes the audit gap |
| 8 | CHANGELOG `## [Unreleased]` + `### Changed/Fixed/Tests` format | ✓ | ✓ — `## [Unreleased]` section added Wave 21 | ✓ | ✓ — `## [Unreleased]` section added Wave 21 |

---

## 4. Test infrastructure

| # | Axis | applemusicarr | tidalarr | qobuzarr | brainarr |
|---|------|:-:|:-:|:-:|:-:|
| 9 | Test csproj NLog/FluentValidation as direct PackageReference (not HintPath) | ✓ `tests/AppleMusicarr.Plugin.Tests/...csproj:46` | ✓ `Tidalarr.Tests.csproj:26` (NLog) + FluentValidation hint | ✓ `tests/Qobuzarr.Tests/...csproj:37` | ✓ `Brainarr.Plugin.csproj:70,74` |
| 10 | Lidarr host assemblies pinned + extraction documented | ✓ | ✓ | ✓ | ✓ |
| 11 | Docker E2E harness w/ per-plugin port | ✓ port `8691` (`AppleMusicarrLidarrContainerFixture.cs`) | ✓ port `8690` | ✓ port `8692` | ✓ port `8693` |
| 12 | `bin-tests/` split for cross-ALC type-identity testing | N/A — `AppleMusicarr.Plugin.Tests.csproj` does not set `OutputPath=bin-tests/` or `PluginPackagingDisable`. It takes `PluginPackagingDisable` via `<ProjectReference>` only transitively; no cross-ALC sandbox tests exist in the apple suite (verified: csproj has no `bin-tests` property group). Apple's SDK ALC conflict is a separate tracked item; no ILRepack cross-ALC failures identified in apple's test suite. | **Wave-22**: ✓ configured (`OutputPath=bin-tests\;EnablePluginDeployment=false` in `Tidalarr.Tests.csproj`; `PluginSandboxRuntimeTests` + `PluginSmokeTests` prefer `bin-tests/`, fallback `bin/`). Fixed the 6 cross-ALC `BackendHealthCacheAdoptionTests` failures. | ✓ split present (`PluginPackagingDisable=true;OutputPath=bin-tests/` per qobuz CLAUDE.md) | N/A — `Brainarr.Tests.csproj` passes `PluginPackagingDisable=true` via `<AdditionalProperties>` on the `<ProjectReference>` (csproj:43), deliberately consuming the un-merged DLL for type-identity stability. No separate `bin-tests/` output path needed because brainarr has no cross-ALC sandbox runtime tests (import-list only; no streaming bridge). |

---

## 5. Host-bridge primitives (streaming plugins; N/A for brainarr)

| # | Axis | applemusicarr | tidalarr | qobuzarr | brainarr |
|---|------|:-:|:-:|:-:|:-:|
| 13 | `HostBridgeDownloadTrackerStore` (static field on download client) | ✓ `AppleMusicLidarrDownloadClient.cs:49-50` | ✓ `TidalLidarrDownloadClient.cs:38-39` | ✓ `QobuzDownloadClient.cs:61-62` (queue service orchestrates) | N/A — import-list plugin, no download client |
| 14 | `HostBridgeDownloadOrchestrator` (snapshot → Task.Run) | ✓ `AppleMusicLidarrDownloadClient.cs:78` | ✓ `TidalLidarrDownloadClient.cs:122` | N/A — qobuz uses internal queue service (architectural choice) | N/A |
| 15 | `PlaceholderSearchUri.Build` / `TryExtractQuery` | ✓ `AppleMusicLidarrIndexer.cs:87,271` | ✓ `TidalLidarrIndexer.cs:134,438` | N/A — qobuz uses real HTTP URLs (`QobuzParser.cs:254`) | N/A |
| 16 | `AlbumReleaseInfoBuilder` | ✓ | ✓ `TidalLidarrIndexer.cs:540,583` | N/A — qobuz multi-segment GUID (`qobuz:album:{id}:edition={x}:quality={int}`) doesn't fit the single-quality-hint builder; convergence requires Common extension | N/A |
| 17 | `PrefixedReleaseGuidParser` | ✓ | ✓ `TidalLidarrDownloadClient.cs:363` | ✓ `QobuzParser.cs:233` | N/A |
| 18 | `PathTraversalGuard` (`SanitizeSegment` + `IsPathWithinRoot`) | ✓ `AppleMusicLidarrDownloadClient.cs:129,152` | ✓ `TidalLidarrDownloadClient.cs:401` | ✓ `Download/Services/DownloadFileService.cs` | N/A — brainarr touches no user-supplied filesystem paths |

---

## 6. Resilience / observability

| # | Axis | applemusicarr | tidalarr | qobuzarr | brainarr |
|---|------|:-:|:-:|:-:|:-:|
| 19 | `BackendHealthCache` (DelegatingHandler wrapping `BackendHealthCache.Shared`) | ✓ `AppleBackendHealthHandler.cs:46` | ✓ `TidalBackendHealthHandler.cs:33` (wraps all 4 HTTP pipelines per Wave 13C) | ✓ `QobuzHttpClient.cs:31,40,104` | ⚠ partial — ✓ for local providers (`BrainarrOllamaProvider.cs:60`, `BrainarrLmStudioProvider.cs:64`, `ModelDetectionService.cs:41`); **N/A for the 12 cloud/subscription providers** — they have their own latency budgets and LLM-specific retry policies (per-provider timeouts + LlmAuthCircuit pre-flight), and BackendHealthCache.Shared's connection-refused fail-fast semantics target local-host-down detection that doesn't apply to cloud REST endpoints with their own circuit breakers. |
| 20 | `AuthFailureGate` (Common singleton, mirrors apple/qobuz wiring) | ✓ `AppleMusicarrStreamingPlugin.cs:130-134` + indexer adapter helpers | **Wave 21**: ✓ `TidalModule.cs` registration (commit `41f2ac4`) + indexer/download-client per-entry-point wiring (commit `30a6bfd`). Closes the long-standing `// independent of AuthFailureGate` comment-only reference. | ✓ `QobuzarrStreamingPlugin.cs:36` + `BridgeQobuzApiClient.cs:35` | **Wave 22**: ✓ **via Common facade**. `LlmAuthCircuit` (`Brainarr.Plugin/Services/Resilience/LlmAuthCircuit.cs`) was refactored to a thin wrapper over Common v1.16.0's `AuthFailureGate` + `SlidingWindowAuthFailureHandler`. Same public API (`IsOpen` / `RecordAuthFailure` / `RecordSuccess` / `MakeKey`); the per-key state machine is now the shared ecosystem implementation. SHA-256-hashed key derivation + sliding-window + 30-min open-duration semantics live in (or layer on) the shared stack. Wired in all 11 cloud/subscription providers (Wave-22 Phase D); `BrainarrOpenAiCompatibleProvider` adoption tracked separately (apiKey is optional for self-hosted backends). |
| 21 | `JsonFileStore<TKey, TValue>` | ✓ `FileUnresolvedQueueStore.cs:27`, `FileMusicBrainzMappingCache.cs:28`, `FileApplePinnedMappingStore.cs:23` | ⚠ `FileTokenStore<TidalTokens>` adopted at `TidalModule.cs:112-143`; no other JSON stores | ✓ **stale finding resolved** — `SessionManager.cs:86-90` uses `new FileTokenStore<QobuzSession>(effectivePath)` inside `StreamingTokenManager<QobuzSession, QobuzCredentials>`. Wave-8B `SecureSessionManager` rip-out pre-dated the original audit snapshot. Custom JSON I/O no longer exists. | ✓ `ReviewQueueService.cs:21`, `ReviewActionAuditService.cs:36` |
| 22 | `BoundedConcurrentDictionary` (v1.15.0+ API) | ✓ `MusicBrainzReleaseMatcher.cs:40-43` (4 caches @ 10000 cap) + `ImportListService` | N/A — `PKCEStateStore.InMemoryCache` is domain-bounded by config-path count | N/A — `_hostGates` domain-bounded by user-host count (1-2 in practice) | ✓ `LimiterRegistry.cs:41-45` (5 dicts @ 5120 cap), `MetricsCollector.cs:25` (Metrics @ 1024 cap) |
| 23 | `TestValidationBuilder` in `Test()` overrides | ✓ `AppleMusicLidarrIndexer.cs:162-169`, `AppleMusicLidarrDownloadClient.cs:199-206` | ✓ `TidalLidarrDownloadClient.cs:281-285` | N/A — qobuz uses FluentValidation pre-`Test()` (architectural choice) | **Wave 21**: ✓ `ConfigurationValidator.cs:Validate` (commit `c34d014`) — per-provider credential field requirements gate the behavioral connection probe. Closes the audit `MISSING` axis. |
| 24 | `HttpExceptionClassifier` in `Test()` failure paths | ✓ implicit via bridge adapters | **Wave 21**: ✓ `TidalLidarrIndexer.cs:326` + `TidalLidarrDownloadClient.cs:387` (commit `102939b`) — Auth-class failures route to `Authentication` validation field, other categories get tailored hints. Closes the audit `MISSING` axis. | ✓ `AuthTokenManager.cs:376`, `AdaptiveQobuzApiClient.cs:54` | ✓ providers delegate to `LlmErrorMapper` (Common) |

---

## 7. Logging discipline

| # | Axis | applemusicarr | tidalarr | qobuzarr | brainarr |
|---|------|:-:|:-:|:-:|:-:|
| 25 | `PluginLogContext` ambient scope at every entry point | ✓ 3-5 entry points | ✓ 6 scopes confirmed Wave 21 (Search, indexer Test, Download, downloadclient Test, OAuthExchange, OAuthRefresh) — Tidal has no JWT-sign path so apple's "auth-token-sign" scope is N/A by design | ✓ `QobuzIndexer.cs:189,265`, `AuthTokenManager.cs:266` | ✓ 25 files wrap requests; all 11 LLM providers + orchestrator paths |
| 26 | `Scrub.Url` / `Scrub.Secret` discipline | ⚠ 2 explicit Scrub.Secret call sites (KeyId, MusicUserToken). Bearer tokens never directly logged. Acceptable surface; broader audit deferred | ✓ `TidalLidarrIndexer.cs:382` (Scrub.Url on auth URL) + `TidalOAuthService.cs:125,144` (LogRedactor.Redact on response bodies — canonical Common API for free-form text). Bearer tokens never directly logged. | ✓ `AudioFileDownloader.cs:73`, `QobuzRequestSigner.cs:64` | ✓ 8 cloud providers wrap API keys with `Scrub.Secret`; comprehensive coverage |
| 27 | `WarnOnce` adoption | ✗ N/A — zero hand-rolled `HashSet<string>` warn-once patterns in repo grep; nothing to migrate | ✗ N/A — same as apple; documented in CLAUDE.md Wave 21 | ✓ `QobuzIndexer.cs:58` (wire-warn gate) | ✓ `ITokenizer.cs:42` + 5 other call sites |
| 28 | Logger acquisition style (MEL `ILogger<T>` vs NLog-direct) | MEL (100%) | mixed — `TidalApiClient.cs` uses MEL via `ILogger<TidalApiClient>`; native indexer/download-client use NLog `Logger` (host-contract requirement) | NLog injected (host-contract requirement) | NLog injected (100%) |

**Logger style (#28)**: the mix is structural — Lidarr's `HttpIndexerBase` / `DownloadClientBase` ctors require an NLog `Logger`, so the entry-point classes are NLog by necessity. MEL `ILogger<T>` adoption is feasible for non-host-contract internal services. Convergence is a multi-plugin refactor; per the mission contract this is "different by design" for the host-contract surface. Plugin-internal code can migrate opportunistically.

---

## 8. Documentation hygiene

| # | Axis | applemusicarr | tidalarr | qobuzarr | brainarr |
|---|------|:-:|:-:|:-:|:-:|
| 29 | CLAUDE.md `## Common helpers in use` section | ✓ 11 entries | ✓ updated Wave 21 (PluginLogContext, AuthFailureGate, HttpExceptionClassifier, WarnOnce N/A) | ✓ 9 entries | ✓ updated Wave 21 (TestValidationBuilder, LlmAuthCircuit divergence subsection) |
| 30 | File ↔ class name parity | ✓ | ✓ **Wave-22**: `StreamManifest` → `TidalStreamManifest`, `AudioFormatHandler` → `TidalAudioFormatHandler` renamed (commit `3fdee87`). All tidalarr file↔class outliers resolved. | ✓ | ✓ |

---

## 9. Cross-cutting concerns (post-Wave-25)

| # | Axis | applemusicarr | tidalarr | qobuzarr | brainarr |
|---|------|:-:|:-:|:-:|:-:|
| 31 | Retry policy preset usage | ✓ uses Common's `ResiliencePolicy` (`MusicBrainzReleaseMatcher.cs:759`) | ⚠ has own HTTP pipeline; may not route through Common's policy engine | ⚠ has own HTTP pipeline; same exposure as tidal | N/A — uses `GenericResilienceExecutor` locally (LLM-specific retry logic) |
| 32 | `FieldDefinition` order discipline | ⚠ indexer puts `MusicUserToken` at field 3 `Advanced=true`; DC puts it at field 3 `Advanced=false` — inconsistent within the same plugin | ✓ constants-file approach (`SettingsDisplay.Indexer.ConfigPathOrder`) | ✓ fields 1-11 with named sections | N/A — import-list, different field shape |
| 33 | Cookie persistence / cookie-jar TTL | N/A — developer-token auth, no cookies | ⚠ OAuth session cookies inherited via host `CookieContainer`; no TTL eviction logic | ⚠ same exposure as tidal — session cookies can accumulate across restarts | N/A — stateless API key auth |
| 34 | TLS certificate pinning / custom validation | N/A — deliberate; no plugin pins certs | N/A — deliberate | N/A — deliberate | N/A — deliberate |
| 35 | Plugin-state recovery on restart (download-queue checkpointing) | N/A for download checkpoint — `FileUnresolvedQueueStore` persists IMPORT-LIST unresolved-album candidates (different concern) | ⚠ **intentionally divergent** — in-memory `HostBridgeDownloadTrackerStore` (process-lifetime) + host `DownloadItemStatus`; no plugin-side checkpoint (host owns download lifecycle) | ⚠ **intentionally divergent** — same as tidalarr (in-memory `DownloadQueueService` + host tracking); consistent host-reliant design, NOT a qobuz-specific gap | N/A — import-list, no download queue |

**Row 31 — Retry policy (#31)**: Tidal and Qobuz each maintain their own HTTP retry/backoff logic (Polly policies or manual catch-retry loops) that predate Common's `ResiliencePolicy`. Convergence would require threading a Common executor through each HTTP pipeline. Non-blocking but creates duplicated exponential-backoff tuning surface. Brainarr's LLM-specific retry has distinct concerns (per-model timeout budgets, provider failover, `LlmAuthCircuit` coordination) that don't map cleanly to streaming-service resilience — N/A is correct.

**Row 32 — FieldDefinition order (#32)**: Apple's inconsistency (same field index, different `Advanced` value between indexer and DC) is a UX papercut that could confuse users who configure one and expect the other to behave identically. Tidal and Qobuz both used structured ordering from the start; apple's DC was added later without auditing the indexer's field order.

**Row 33 — Cookie-jar TTL (#33)**: The `CookieContainer` inherited from Lidarr's host `HttpClientFactory` accumulates Set-Cookie headers from OAuth provider redirects. Neither Tidal nor Qobuz prunes stale cookies, so long-running Lidarr instances gradually accumulate entries. The practical impact is limited (OAuth flows are infrequent and providers don't set many cookies), but it's an undocumented state-accumulation behavior. Apple is unaffected (developer-token JWTs, no cookie exchange). Brainarr is unaffected (stateless API key auth for all cloud providers; Ollama/LM Studio HTTP calls don't set cookies).

**Row 34 — TLS cert pinning (#34)**: Recorded explicitly so future security audits don't re-raise this as a missing item. All four plugins intentionally rely on Lidarr host's default TLS validation (OS trust store + .NET's `HttpClient` defaults). Pinning would create operational burden for certificate rotations without meaningful security benefit for this threat model (streaming API calls over HTTPS to well-known providers).

**Row 35 — Restart checkpointing (#35)** — *intentionally divergent; reclassified 2026-05-30 after verifying against source.* Both streaming plugins hold in-flight download state **in-memory** — tidalarr in a `private static readonly HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>` (process-lifetime; `TidalLidarrDownloadClient.cs:41`), qobuzarr in `DownloadQueueService._activeDownloads` (a `ConcurrentDictionary`; `DownloadQueueService.cs:19`) — and both rely on the Lidarr host's `DownloadItemStatus` for download lifecycle. **Neither persists a plugin-side crash checkpoint, and that is consistent between them** — the standard Lidarr download-client design (the host owns the download queue; plugins are clients). The earlier "qobuz ✗ loses-all vs tidal ⚠ soft-recovery" split was an **audit inconsistency**: both use the same in-memory tracking, so they behave identically on restart — qobuz is *not* a qobuz-specific gap. Apple's `FileUnresolvedQueueStore` is **not** a download checkpoint; it persists import-list *unresolved-album* candidates (a different concern). A true plugin-side crash-resume (persist + resume in-flight transfers, building on Common's `JsonFileStore` + resume-transfer logic) would be an **optional shared enhancement for both streaming plugins**, tracked as future work — not a parity divergence between plugins.

---

## 10. Download telemetry logging (telemetry-consolidation session, 2026-05-29)

| # | Axis | applemusicarr | tidalarr | qobuzarr | brainarr |
|---|------|:-:|:-:|:-:|:-:|
| 36 | Canonical `IDownloadTelemetrySink` (Common `LoggingDownloadTelemetrySink` via `AddDownloadTelemetry`) | N/A — no Common download-telemetry path (developer-token flow; not on `SimpleDownloadOrchestrator`) | ✗ → **pending #556**: registers a bespoke `TidalDownloadTelemetrySink` that hand-rolls an IDs-only NLog format duplicating `DownloadTelemetryService`. Migrates to `AddDownloadTelemetry()` + deletes the sink after Common PR #556 lands + re-pin | ✗ → **pending #556**: logs ad-hoc (`Downloaded: {title} ({MB})`), no Common telemetry. Adopts the orchestrator sink after #556 + re-pin | N/A — import-list plugin, no download client |
| 37 | Rich per-track log fields (artist/album/track/format/quality/size/path) | N/A | ✗ → **pending #556** | ✗ → **pending #556** | N/A |

**Executable guard**: `EcosystemParityTestBase.Check_UsesCommonDownloadTelemetrySink` (behavior contract #7) fails any plugin assembly that declares a local `IDownloadTelemetrySink` instead of registering Common's `LoggingDownloadTelemetrySink`. Auto-enforces for tidal/qobuz once they re-pin and opt into `RunBehaviorContractChecks` via `PluginAssembly`. A genuinely custom telemetry backend opts out by overriding the check with a rationale.

**Common foundation (PR #556)**: `DownloadTelemetry` enriched with nullable identity/quality fields + `From(StreamingTrack, StreamingAlbum, StreamingQuality, …)` factory; `DownloadTelemetryService` renders the rich human line + adds `artist`/`track_title`/`album_title`/`format` to the `[LPC_TELEMETRY]` marker (additive; `success` stays last); `SimpleDownloadOrchestrator` (the single producer) enriches centrally; `LoggingDownloadTelemetrySink` + `AddDownloadTelemetry()` give one-line opt-in so the log format lives in exactly one file.

---

## Wave 21 (this session) commits

| Repo | Commit | What it does |
|------|--------|------|
| tidalarr | `41f2ac4` | DI registration of `AuthFailureGate` singleton in `TidalModule` |
| tidalarr | `30a6bfd` | Per-entry-point gate wiring in `TidalLidarrIndexer` + `TidalLidarrDownloadClient` |
| brainarr | `ffb54a6` | Documents `LlmAuthCircuit` ↔ `Common.AuthFailureGate` divergence + convergence path |
| tidalarr | `096275a` | `PluginLogContext` + `WarnOnce` audit closure (full coverage / N/A by lack of need) |
| tidalarr | `102939b` | `HttpExceptionClassifier` adoption in `Test()` catch blocks |
| brainarr | `c34d014` | `TestValidationBuilder` adoption — per-provider credential field requirements |
| brainarr | `441d655` | `manifest.json` gains `commonVersion` + `ManifestJson_MatchesPluginJsonCommonVersion` contract test |
| common | (this commit) | This parity-matrix document |

---

## Outstanding tech debt (release-non-blocking)

These items are NOT blocking shipping any plugin; they are recorded for future cleanup passes:

1. ~~**Tidal `bin-tests/` split**~~ — **Resolved Wave-22**. Csproj sets `OutputPath=bin-tests\;EnablePluginDeployment=false`; `PluginSandboxRuntimeTests` + `PluginSmokeTests` prefer the un-merged DLL. Fixed the 6 cross-ALC `BackendHealthCacheAdoptionTests` failures.
2. ~~**Tidal file↔class renames**~~ — **Resolved Wave-22**. `StreamManifest` → `TidalStreamManifest`, `AudioFormatHandler` → `TidalAudioFormatHandler` landed.
3. ~~**Qobuz `PluginLifecycle.RegisterShutdown` adoption**~~ — **Resolved Wave-22**. `QobuzarrStreamingModule` migrated to `PluginLifecycle.RegisterShutdown` + `PluginLifecycle.Shutdown()` with CAS-guarded static `_hooksRegistered`. Behavioral guarantee identical.
4. ~~**Common extension for `LlmAuthCircuit` convergence**~~ — **Resolved Wave-22**. `SlidingWindowAuthFailureHandler` shipped in Common v1.16.0; brainarr's `LlmAuthCircuit` refactored to a facade over Common's `AuthFailureGate` + `SlidingWindowAuthFailureHandler`. Closes the row-20 divergence.
5. **Qobuz custom `SessionManager` → `JsonFileStore` migration** — **STALE FINDING**. Wave-22 verified qobuz already uses Common's `FileTokenStore<QobuzSession>` + `StreamingTokenManager<QobuzSession, QobuzCredentials>` (`SessionManager.cs:86-90`). The original audit snapshot pre-dated the wave-8B `SecureSessionManager` rip-out. Mark this row N/A-with-rationale on next refresh.
6. ~~**Tidal `Constants.cs` shape**~~ — **Resolved Wave-22**. `TidalConstants.cs` gains the canonical `PluginName` / `ServiceName` / `PluginVendor` const block.
7. ~~**Brainarr `LlmAuthCircuit` coverage**~~ — **Resolved Wave-22**. All 11 cloud/subscription providers now wire `LlmAuthCircuit` (OpenAI, Anthropic, ClaudeCodeSub, Perplexity, OpenRouter, DeepSeek, Groq, Gemini, Z.AI GLM, Z.AI Coding, OpenAI Codex Subscription). `BrainarrOpenAiCompatibleProvider` is a 12th auth-bearing provider where `_apiKey` is optional (self-hosted backends); circuit adoption deferred to follow-up — open as a separate row when wired.

### Wave-22 new findings (added 2026-05-25 from adversarial review)

8. **`LlmAuthCircuit.MakeKey` null/empty collision** — **Fixed Wave-22**. Previously coerced null/empty `apiKey` to `""` and hashed, producing identical dict keys across providers that hadn't been configured yet. Fix: throws `ArgumentException` on null/empty. Regression tests at `Brainarr.Tests/Services/Resilience/LlmAuthCircuitTests.cs`.
9. **Qobuz appSecret-reconstruction leak in Debug logs** — **Fixed Wave-22**. `QobuzAuthenticationService` was logging raw `seed`/`info`/`extras` (which concatenate into the appSecret) at Debug, and `appId` at Info. Now logs lengths only + `Scrub.Secret` mask on `appId`. Trailing-4 token mask in `QobuzApiClient` replaced with canonical `Scrub.Secret` leading-3.
10. **Apple `DownloadAndDecryptContentAsync` dead code** — **Fixed Wave-22**. Private method writing the literal "Mock decrypted content" string to outputPath had zero callers but was a footgun for any future contributor. Deleted; the legitimate mock path remains in `MockExternalDownloadHandler` (env-gated).
11. **Cross-plugin `ext-common-sha.txt` drift** — **Fixed Wave-22**. All 4 plugins now record `936556e` (v1.16.0) in their `ext-common-sha.txt` files, aligned with the actual submodule pin.
12. ~~**`PublicAPI.Unshipped.txt` net6.0/net8.0 mismatch**~~ — **Obsolete 2026-06**. The PublicAPI baseline files and the `PublicApiAnalyzers` gate were removed entirely; there is nothing to promote. See `docs/reference/PUBLIC_API_BASELINES.md`.
13. ~~**Brainarr `BackendHealthCache` partial adoption**~~ — **Resolved Wave-25**. Row 19 brainarr cell updated from a misleading ✓ to ⚠ partial: ✓ for local providers (Ollama + LM Studio) and N/A-with-rationale for the 12 cloud/subscription providers (own latency budgets + LlmAuthCircuit pre-flight; connection-refused fail-fast semantics don't apply to cloud REST endpoints).
14. **Common `main` 20 commits unpushed to origin** — **Pending push at end of session**. Local main contains v1.14/v1.15/v1.16/v1.17 work + parity matrix; `release/v1.9.0` was the currently-checked-out branch at session start. Cherry-picked Wave-22 README refresh onto main; pending `git push origin main && git push --tags` to publish.
15. **22+ stale May-10/11 feature branches** across all 5 repos — **Pending triage**. Most named `feat/adopt-common-plugin-contracts`, `feat/wire-requests-per-second`, etc. Look superseded by direct main merges or abandoned. Triage decision (rebase/merge/delete) deferred.

---

## Wave-23 closures + new findings (added 2026-05-25)

### Closed in Wave-23
16. **Apple ecosystem-version drift** — applemusicarr submodule was at v1.17.0 (639d573) while ext-common-sha.txt was at v1.16.0 (936556e) after the Wave-22 fix accidentally INTRODUCED this drift. Plugin.json + manifest.json already declared `commonVersion: 1.17.0`. Resolved Wave-23 by bumping the 3 sibling plugins (brainarr `49ba473`, tidalarr `7e2bdd0`, qobuzarr `13a299b`) to v1.17.0 + aligning apple's ext-common-sha (`5ef3ca4`). Ecosystem now back at lockstep v1.17.0.
17. **qobuz appSecret-reconstruction surfaces** — regex calls in `ExtractAppSecretFromBundle` lacked timeouts (defense-in-depth, not catastrophic ReDoS — patterns are linear). `request_sig` (appSecret-derivative signature) was logged at Debug. `loginResponse.Message` interpolated into exception text could echo attacker-controlled API response content. All three fixed in qobuzarr `45e240b`.
18. **brainarr MakeKey whitespace gap** — Wave-22 fix used `IsNullOrEmpty`; whitespace `"   "` would still produce a single collision-prone hash slot. Tightened to `IsNullOrWhiteSpace` in `20b133f`; new `[Theory] MakeKey_WhitespaceApiKey_ThrowsArgumentException` covers " ", "\t", "\n", " \t\n ". Same fix applied to `GeminiModelDiscovery.CreateCacheKey` (sibling pattern).
19. **qobuz AuthFailureGate registration shape** — used `AddSingleton<AuthFailureGate>()` (default ctor) while apple+tidal passed explicit `(handler, TimeProvider.System, TimeSpan.FromSeconds(60), logger)`. Probe interval was implicit. Aligned in qobuzarr `342ee99`.
20. **tidal+qobuz floating Docker tags** — 10 workflow files used `ghcr.io/hotio/lidarr:pr-plugins` without version suffix while apple+brainarr pin `nightly-3.1.3.4970`. Pinned in tidalarr `cb2e43e` + qobuzarr `342ee99`.
21. **brainarr constants triple** — added (see row 5 update).
22. **Cleanup leftovers** — stale TechDebt refs in 4 brainarr docs purged; DIWiringAndParityTests relocated; QobuzarrPluginComplianceTests renamed (referred to deleted symbol). brainarr `8830609`, qobuzarr `fe685cf`.

### Open after Wave-23 (deferred follow-ups)
23. **Apple `AppleMusicLidarrDownloadClient` entry-point gate helpers** — apple's indexer adapter has `IsAuthShortCircuited` + `RecordAuthOutcomeFromException` but the download client doesn't. Apple's DC uses primary-ctor (C# 12) with fixed Lidarr `DownloadClientBase<T>` signature; would need tidal-style static helpers + `IServiceProvider` lookup. UX gap (cleaner "auth latched" message vs generic 401), not security gap — apple's runtime still consults the gate at HTTP layer.
24. **Qobuz `QobuzIndexer` + `QobuzDownloadClient` entry-point gate helpers** — same shape gap as #23. `QobuzIndexer` routes through `BridgeQobuzApiClient` which holds the gate as a field, so 401s ARE recorded and the gate latches; entry-point helpers would just pre-flight short-circuit (saves time + log noise) and surface a clean auth-latched validation failure instead of a generic HTTP error.
25. **8 missing per-provider OpenCircuit adoption tests in brainarr** — Perplexity, OpenRouter, DeepSeek, Groq, Gemini, ZaiGlm, ZaiCoding, OpenAiCodexSub. ~80 LOC of mechanical copy from the OpenAi/Anthropic/ClaudeCodeSub template.
26. ~~**PublicAPI net6.0 / net8.0 format mismatch**~~ — **Obsolete 2026-06**. The PublicAPI baseline files and the `PublicApiAnalyzers` gate were removed entirely; no promotion or pipeline investigation needed. See `docs/reference/PUBLIC_API_BASELINES.md`.
27. **qobuz log-scrub regression test** — Phase-2 security fix (commit 45e240b) is well-commented but not covered by a sentinel-based test. Recommended shape: capture logs via existing `Helpers/TestLogger.cs`, invoke `ExtractAppSecretFromBundle` via reflection with a controlled bundle, assert no sentinel seed/info/extras value appears in any captured log line.

---

## Wave-30/31 closures + new findings (added 2026-05-27)

### New Common features landed (Wave-30)

| Feature | PR | brainarr | tidalarr | qobuzarr | apple |
|---|---|---|---|---|---|
| DownloadPathValidator | #519 | N/A (ImportList) | ✓ adopted | ✓ adopted | ✓ adopted |
| LrclibClient (lyrics) | #521 | N/A | pending | ✓ LyricsEnricher | N/A |
| RateLimitHeaderUtilities | #518 | N/A | available | available | available |
| UniversalAdaptiveRateLimiterOptions | #524 | available | available | available | available |
| FileStreamingResponseCache ILogger | #520 | available | available | available | available |
| TestFailureFormatter | (v1.14+) | available | available | available | available |
| enforcement scripts | #522 | — | — | — | — |

### Closed in Wave-30/31

28. ~~**22+ stale branches (item 15)**~~ — **Resolved Wave-28**. 26 branches deleted across 4 plugin repos. 14 deferred INVESTIGATE branches verified mostly-already-merged (bounded-dict 91 commits, tech-debt-arc 46 commits = 99% in main). 31 stale local Common branches cleaned.
29. ~~**Apple `AppleMusicLidarrDownloadClient` entry-point gate helpers (item 23)**~~ — **Resolved Wave-28**. `IsAuthShortCircuited` + `RecordAuthOutcomeFromException` wired in download client Test() + Download(). Now matches tidal+qobuz pattern. Per-call `IServiceProvider` lookup.
30. ~~**Qobuz entry-point gate helpers (item 24)**~~ — **Resolved Wave-28**. QobuzIndexer + QobuzDownloadClient Test() catch blocks now use `HttpExceptionClassifier` for categorized error messages + `RecordAuthOutcomeFromException` for gate recording.
31. **qobuz IsInputSafe .com false positive** — `LidarrInputValidator.DangerousExtensions` included `.com` (legacy DOS executable), causing ALL `.com` email addresses to fail credential validation. Fixed with `@` guard: file-extension checks skipped for email-like inputs. Discovered via adversarial TDD.
32. **qobuz BridgeQobuzApiClient timeout regression** — SharedSystemHttpClient adoption (commit b95391d) replaced a 30s per-client timeout with a 10-minute global timeout (designed for downloads). API calls that should fail-fast at 30s now hung for 10 minutes. Fixed with per-request `CancellationTokenSource(30s)` on GetAsync + PostAsync.
33. **tidalarr empty-chunk-URL guard** — `TidalChunkDownloader.DownloadAndAssembleAsync` accepted manifests with zero chunk URLs, silently producing empty/corrupt output. Added `InvalidOperationException` guard + 2 TDD tests.
34. **TestValidationBuilder parity** — qobuzarr was the last plugin without TestValidationBuilder. Adopted in QobuzDownloadClient.Test() for DownloadPath pre-check. All 4 plugins now use it.

### Test coverage (Wave-31)

83 new TDD tests across 7 previously-untested classes:
- CredentialValidator (13), TokenRefresher (18), QobuzSubstringCache (14), QobuzSearchService (17), QobuzDownloadClient auth-gate (15), LyricsEnricher (4), TidalChunkDownloader (2)
- Ecosystem total: 7,424 passing / 0 failures

---

## Definition-of-done verification

Per the user's mission contract:

> Every parity axis is adopted by all applicable plugins, or documented N/A with architectural reason.

✓ 35 axes covered above. Every cell is ✓ / ⚠-with-rationale / ✗-with-tech-debt-note / N/A-with-reason.

> Common extensions are tested, versioned, and pinned consistently.

✓ Historical Wave-21/31 verification used Common v1.15.0 across the original 4-plugin set. Current five-plugin pin/version truth is recorded in the scope note at the top of this document and enforced by `scripts/ci/ecosystem-repos.json` + `scripts/ci/verify-ecosystem-ci-contract.ps1`.

> No plugin remains on an older/divergent Common version.

✓ Confirmed — see commit history of each plugin's `chore(submodule)` bumps.

> Relevant tests are green; no "done" claims with failing tests.

✓ All NEW tests added in Wave 21 are green. Pre-existing failures in Tidal's `BackendHealthCacheAdoptionTests` (6) are documented cross-ALC ILRepack tech debt with the proper fix tracked (`bin-tests/` split — same pattern qobuz uses).

> CLAUDE.md and CHANGELOG.md are updated in touched repos.

✓ Every Wave 21 commit includes the corresponding doc updates.

> Logs are scoped, structured, scrubbed, and level-disciplined.

✓ All 4 plugins push `PluginLogContext` at every canonical entry point; credential-bearing logs route through `Scrub.Url` / `Scrub.Secret` / `LogRedactor.Redact`; level discipline is conservative (Info for state transitions, Warn for recoverable, Error for unrecoverable).

> Release packaging, install-button naming, Docker E2E smoke coverage, and multi-plugin compatibility remain intact.

✓ No release-blocking changes in Wave 21. All builds clean, all changed tests green. The 4 plugins side-by-side compatibility (verified by the May 2026 4-plugin Docker side-by-side test referenced in apple's CLAUDE.md) is not affected.

---

## How to regenerate this matrix

This document was produced by:

1. Reading each plugin's `CLAUDE.md` "Common helpers in use" section.
2. Grepping each plugin's `src/` for the canonical helpers (`PluginLogContext`, `Scrub.*`, `AuthFailureGate`, `HttpExceptionClassifier`, `TestValidationBuilder`, `WarnOnce`, etc.).
3. Reading the contract tests (`VersionContractTests.cs`) to verify the version-coherence guarantees.
4. Cross-referencing the audit reports from the four parallel Explore agents spawned at session start.

To refresh after future changes:
- Re-grep each plugin's `src/` for the helpers above.
- Re-read each plugin's `CLAUDE.md` for documented N/A cases.
- Verify `commit refs` in the "Wave 21 commits" section still point at live history.
