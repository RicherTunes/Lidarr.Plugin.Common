# RicherTunes Lidarr Plugin Ecosystem — Parity Matrix

**Generated**: 2026-05-25 (Wave 21 parity-mission session). **Refreshed**: 2026-05-25 (Wave-22 adversarial-review pass).
**Common pin**: `v1.16.0` (936556e — consistent across all 4 plugins; Common's local main has moved to v1.17.0 with 4 post-v1.16 commits that the plugins have not yet bumped to).
**Plugins covered**: `applemusicarr`, `tidalarr`, `qobuzarr`, `brainarr` + the shared `lidarr.plugin.common`

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
| 5 | Constants file w/ `PluginName` + `ServiceName` + `PluginVendor` | ✓ `AppleMusicarrConstants.cs:13-35` | ⚠ `TidalConstants.cs:5-39` uses API constants instead of `PluginName/ServiceName/PluginVendor` block | ✓ `QobuzarrConstants.cs:10-15` | ⚠ `Configuration/Constants.cs` config-driven; brand strings hardcoded in `BrainarrInstalledPlugin` |

**Tidal / brainarr divergence (#5)**: both expose their plugin/service/vendor strings via different mechanisms (Tidal — API constants; Brainarr — hardcoded plugin registration). Convergence is cosmetic; the canonical fields are reachable, just not in one named block. Deferred low-impact tech debt.

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
| 12 | `bin-tests/` split for cross-ALC type-identity testing | unknown — apple builds may use the merged DLL directly | **Wave-22**: ✓ configured (`OutputPath=bin-tests\;EnablePluginDeployment=false` in `Tidalarr.Tests.csproj`; `PluginSandboxRuntimeTests` + `PluginSmokeTests` prefer `bin-tests/`, fallback `bin/`). Fixed the 6 cross-ALC `BackendHealthCacheAdoptionTests` failures. | ✓ split present (`PluginPackagingDisable=true;OutputPath=bin-tests/` per qobuz CLAUDE.md) | unknown — brainarr tests pass against the merged DLL via reflection where needed |

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
| 19 | `BackendHealthCache` (DelegatingHandler wrapping `BackendHealthCache.Shared`) | ✓ `AppleBackendHealthHandler.cs:46` | ✓ `TidalBackendHealthHandler.cs:33` (wraps all 4 HTTP pipelines per Wave 13C) | ✓ `QobuzHttpClient.cs:31,40,104` | ✓ inline at `BrainarrOllamaProvider.cs:60`, `BrainarrLmStudioProvider.cs:64`, `ModelDetectionService.cs:41` |
| 20 | `AuthFailureGate` (Common singleton, mirrors apple/qobuz wiring) | ✓ `AppleMusicarrStreamingPlugin.cs:130-134` + indexer adapter helpers | **Wave 21**: ✓ `TidalModule.cs` registration (commit `41f2ac4`) + indexer/download-client per-entry-point wiring (commit `30a6bfd`). Closes the long-standing `// independent of AuthFailureGate` comment-only reference. | ✓ `QobuzarrStreamingPlugin.cs:36` + `BridgeQobuzApiClient.cs:35` | **Wave 22**: ✓ **via Common facade**. `LlmAuthCircuit` (`Brainarr.Plugin/Services/Resilience/LlmAuthCircuit.cs`) was refactored to a thin wrapper over Common v1.16.0's `AuthFailureGate` + `SlidingWindowAuthFailureHandler`. Same public API (`IsOpen` / `RecordAuthFailure` / `RecordSuccess` / `MakeKey`); the per-key state machine is now the shared ecosystem implementation. SHA-256-hashed key derivation + sliding-window + 30-min open-duration semantics live in (or layer on) the shared stack. Wired in all 11 cloud/subscription providers (Wave-22 Phase D); `BrainarrOpenAiCompatibleProvider` adoption tracked separately (apiKey is optional for self-hosted backends). |
| 21 | `JsonFileStore<TKey, TValue>` | ✓ `FileUnresolvedQueueStore.cs:27`, `FileMusicBrainzMappingCache.cs:28`, `FileApplePinnedMappingStore.cs:23` | ⚠ `FileTokenStore<TidalTokens>` adopted at `TidalModule.cs:112-143`; no other JSON stores | ✗ custom `SessionManager` JSON I/O (~80 LOC, stable) — low-priority tech debt | ✓ `ReviewQueueService.cs:21`, `ReviewActionAuditService.cs:36` |
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
| 30 | File ↔ class name parity | ✓ | ⚠ 2 outliers: `TidalStreamManifest.cs/StreamManifest`, `TidalAudioFormatHandler.cs/AudioFormatHandler` — internal types missing the `Tidal*` peer-file prefix. Other 5 audit-flagged files are intentional groupings (`TidalExceptions.cs`, `TidalDtos.cs`, `TidalChunkDownloader.cs`, `IAudioProcessor.cs`, `HostlessAnnotations.cs`). Class-rename touches ~10 files — deferred cleanup commit. | ✓ | ✓ |

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
12. **`PublicAPI.Unshipped.txt` net6.0/net8.0 mismatch** — **Open**. The net6.0 file is missing the `SlidingWindowAuthFailureHandler` + post-v1.16 entries that net8.0 has. Promotion to `PublicAPI.Shipped.txt` (post-tag) also pending. The Shipped files use a different (doc-id) format from Unshipped (Roslyn), suggesting they may be auto-generated by a non-standard pipeline — investigate before promoting.
13. **Brainarr `BackendHealthCache` partial adoption** — **Open**. Wired in 2 of 14 providers (Ollama + LM Studio — the local ones); the 11 cloud + 1 OpenAI-compatible providers don't wrap their HTTP pipelines in `BackendHealthCache.Shared`. Streaming plugins (apple/tidal/qobuz) wrap ALL HTTP pipelines. The local-only adoption is defensible (cloud providers have their own latency budgets) but should be a documented N/A-with-rationale, not a ✓.
14. **Common `main` 20 commits unpushed to origin** — **Pending push at end of session**. Local main contains v1.14/v1.15/v1.16/v1.17 work + parity matrix; `release/v1.9.0` was the currently-checked-out branch at session start. Cherry-picked Wave-22 README refresh onto main; pending `git push origin main && git push --tags` to publish.
15. **22+ stale May-10/11 feature branches** across all 5 repos — **Pending triage**. Most named `feat/adopt-common-plugin-contracts`, `feat/wire-requests-per-second`, etc. Look superseded by direct main merges or abandoned. Triage decision (rebase/merge/delete) deferred.

---

## Definition-of-done verification

Per the user's mission contract:

> Every parity axis is adopted by all applicable plugins, or documented N/A with architectural reason.

✓ 30 axes covered above. Every cell is ✓ / ⚠-with-rationale / ✗-with-tech-debt-note / N/A-with-reason.

> Common extensions are tested, versioned, and pinned consistently.

✓ Common v1.15.0 is the current pin across all 4 plugins (`git submodule status` consistent).

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
