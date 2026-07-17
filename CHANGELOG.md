# Changelog - Lidarr.Plugin.Common
<!-- markdownlint-disable MD024 -->

All notable changes to the shared library are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## Quick Links
- **Documentation**: [README.md](README.md) | [Quickstart](docs/quickstart/) | [Architecture](docs/concepts/)
- **Testing**: [TestKit Guide](docs/how-to/TEST_WITH_TESTKIT.md) | [Testing Strategy](docs/testing/)
- **Ecosystem**: [Consuming Plugins](https://github.com/RicherTunes/.github/blob/main/docs/ECOSYSTEM.md)

## Release entry format

Each release entry should include:
- **Upgrade note** – a one-paragraph summary plugin authors can skim.
- **Highlights** – bullets for the most relevant fixes or features.
- Quick facts for breaking changes, deprecations, and dependency updates.
- A [Full diff](...) link comparing the previous tag to the new one.

Template to copy when drafting a release:

```md
### Fixed
- **`PluginPack.psm1` no longer names packages after conditional `<Version>` fallback literals.** The XML fast path read `<Version Condition="...">0.1.0-dev</Version>` verbatim (conditions are not statically evaluable), so a repo whose real version comes from a `VERSION` file via `Directory.Build.props` packaged as `0.1.0-dev` (caught staging qobuzarr v0.5.12). Extracted `Resolve-PluginPackVersion`: the XML fast path is trusted only for condition-free, expression-free nodes; everything else defers to `dotnet msbuild -getProperty:Version`. Self-tested (`scripts/tests/Test-PluginPackVersionResolution.ps1`, wired into both lint jobs).

## [x.y.z] - YYYY-MM-DD
**Upgrade note:** <one-sentence summary>

**Highlights**
- Bullet for key change
- Bullet for key change

**Breaking changes:** None
**Deprecations:** None
**Dependency changes:** None

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/vX.Y.(Z-1)...vX.Y.Z)
```

## [Unreleased]

## [1.18.0] - 2026-07-17
**Upgrade note:** Re-pin to pick up cover-art/lyrics tag embedding, the OAuth-refresh timeout, HLS/CENC/query-optimizer seams, the AttemptV2 durable-queue groundwork, and a dependency-CVE gate that patched two High CVEs; recompile-only for all ecosystem plugins.

**Highlights**
- `IAudioArtworkEmbedder` / `TagLibAudioArtworkEmbedder` + `LyricsEnricher` tag embedding so downloaded albums import into Lidarr with embedded cover art and lyrics.
- OAuth refresh path gains cancellation + a bounded `RefreshTimeout` so a hung auth server no longer wedges every token consumer.
- New shared seams: `HlsManifestParser`, `CencSampleDecryptor`, `HeuristicQueryOptimizer`, `AlbumSizeEstimator`, `MultiQualityReleaseBuilder`, `CappedSearchChain`, and `SimpleDownloadOrchestrator` naming/payload-validation extension points.
- AttemptV2 queue contract + durable-removal WAL groundwork in `HostBridge` (opt-in; LegacyV1 default is byte-for-byte unchanged).
- New dependency-CVE CI gate; first run patched CVE-2026-33116 / CVE-2026-26171 (both High).

**Breaking changes:** Numeric-only — `SearchStopPolicy` enum members were renumbered (`AccumulateAll` moved `0 → 1`, `Unknown = 0` added) and `SearchPlanExecutor` now throws on `Unknown`/undefined values instead of defaulting to accumulate-all. Callers that reference members by name (all ecosystem plugins) are unaffected on recompile; only code relying on the persisted raw integer `0` needs migration.
**Deprecations:** `IAuthFailureGateRegistry` / `AuthFailureGateRegistry` marked `[Obsolete(error: false)]` (scheduled for removal in v2.0.0).
**Dependency changes:** Added a direct floor `PackageReference` on `System.Security.Cryptography.Xml` 8.0.3 (patches transitive 8.0.2; both High CVEs). Retired the `Microsoft.CodeAnalysis.PublicApiAnalyzers` package and its baselines.

### Added
- **AttemptV2 durable removal coordinator (queue-v2 phase 5B)** (`Lidarr.Plugin.Common.HostBridge`) — `deleteData: true` under AttemptV2 now performs a real, crash-recoverable deletion instead of returning `QUEUE_REMOVAL_DEFERRED`. `RemoveAttemptAsync` drives a two-phase transaction over the 5A primitives: WAL `Prepared` → atomic move of the owned tree into `.lpc-trash/<operationId>` (the point of no return that frees the OutputPath) → CAS `Quarantined` → remove the queue mapping → CAS `Deleting` → recursive delete → CAS `Deleted` → journal purge. All guards run under the membership + mutation locks so the cross-attempt re-grab guard stays atomic through the quarantine move: targets outside the owned root (root itself, siblings, `..` escapes) are retained with `QUEUE_SAFE_ORPHAN_OUTSIDE_ROOT`; reparse points (target/root/nested/dangling links, and removal-time root-identity drift) are retained with `QUEUE_SAFE_ORPHAN_LINK_TRAVERSAL`; an active same-path owner keeps its files (state-only removal); a missing in-root source is a successful no-op. New `HostBridgeDownloadTrackerStore.RecoverPendingRemovalsAsync` replays or abandons interrupted deletions at startup — keyed purely on whether the quarantine directory still exists, so it never re-deletes a live OutputPath a restart may have re-grabbed (present ⇒ resume delete; absent ⇒ abandon via `ManualAcknowledgement`) — and is idempotent across repeated runs. Supporting hardening: `FileRemovalJournal.OpenAsync` now runs a bounded sweep of crash-abandoned `*.json.tmp.*` temps (a single orphan previously bricked the journal as `Corrupt`), a new bounded `PurgeAsync` keeps the WAL empty in steady state, and `RelativeStagingPath` + the journal's canonical quarantine path are now forward-slash canonical on every OS (accepting both separators on input) so state files round-trip Windows↔Linux. AttemptV2 also gains a terminal-item count high-water (`HostBridgeQueueStoreOptions.TerminalRetentionHighWater`, default 5000, oldest-terminal-first) so a burst of completions cannot drift into the hard 10k/16 MB persistence bound; TTL eviction stays off for AttemptV2 by design (terminal items are UI/audit evidence) and non-terminal items are never evicted, preserving restart-recovery evidence. New tests un-skip the 11 ready-made 5A removal specs, flip the two neutralization pins (deleteData now deletes), and add a coordinator crash matrix (five FS phases + a WAL write fault) proving no orphaned files, no double-deletes, and idempotent recovery. **LegacyV1 remains byte-for-byte unchanged.**
- **templates/lidarr-plugin: a 6th plugin is now born with the ecosystem guards.** The scaffold gains (1) `MyPluginEcosystemParityTests` — an `EcosystemParityTestBase` subclass with the load-bearing `PluginAssembly` override (without it 14/16 behavior checks silently skip) and `[Trait("Category","Parity")]`, following the tidalarr/qobuzarr adoption pattern; (2) the root files that suite pins: `Directory.Build.props` (ILRepackEnabled=false, VERSION-file versioning, SourceLink, Deterministic, ext/ CPM exclusion), `Directory.Packages.props` (CPM + the six host-coupled canonical pins declared up front), `global.json` (8.0.100/latestFeature), `VERSION`, and the previously-missing `plugin.json` fields (homepage/license/tags/targetFramework/rootNamespace); (3) CI scaffolds — `.gitea/workflows/ci.yml` (secret scan, submodule pin guard, shared lint runner, dependency-CVE scan via `check-vulnerable-packages.ps1`, build+test verify) plus the `github.server_url`-guarded `.github/workflows/ci.yml` mirror, modeled on tidalarr's live workflows; and (4) `services.AddBridgeDefaults()` in the module scaffold so `Check_RegistersBridgeDefaults` passes from day one. The smoke script now additionally asserts the parity class + workflows + root files materialize, structurally validates both workflows (on:/jobs:/tokenization/github guard), and re-runs the materialized test project filtered to `Category=Parity` requiring >=1 executed test (a silently-dropped parity class can no longer fake-green the smoke). Honest limitation (documented in the template README): the smoke cannot execute the workflows themselves — they call Common-submodule scripts that exist only after the author vendors `ext/Lidarr.Plugin.Common`.
- **Behavior-pinning tests for four previously-untested public APIs.** `BackendHealthDelegatingHandler` (stubbed inner handler + `FakeTimeProvider`: pass-through, mark-down only on connection-class failures, fast-fail short-circuit inside the grace window, grace expiry + success clearing the down-state, per-baseUrl cache slots under one provider, fixed-provider labeling), `NamedServiceRateLimiter` (blank-service normalization to the canonical name, shortcut helpers, dispose contract: query methods throw / record methods become silent no-ops / wrapped inner limiter disposed too), `PerformanceMonitor` (metric aggregation per kind, error/cache-hit rates, 1000-event buffer bound, `Reset`, exactly-one final flush on `Dispose`; the periodic timer is parked with a 1-hour interval so no wall-clock races), and `HealthCheckHelper` (healthy/unhealthy/exception result shapes, default + custom error codes, message transform, `OperationCanceledException` rethrow, token forwarding, and the characterized capability-drop asymmetry on the exception path).
- **CLI framework test lane in CI (audit C-10).** The six CLI test files (`CLIServicesTryGetTests`, `ConfigCommandTests`, `JsonConfigServiceTests`, `MemoryQueueServiceTests`, `FileStateServiceTests`, `LiveDashboardTests`) are `Compile Remove`d from the test project unless `-p:IncludeCLIFramework=true`, and no workflow ever set that property — those tests had never compiled or run in CI. Both CI workflows (Gitea + guarded GitHub mirror) now run a dedicated `Test (CLI framework lane, Category=CLI)` step with `IncludeCLIFramework=true`; the six classes carry a new `Category=CLI` trait (added to the Common trait policy) so the lane runs exactly that slice. Enabling the lane immediately surfaced the two `LiveDashboard` bugs below and retired the last `State=Quarantined` tests in the repo (three revived as deterministic tests, two deleted as tautological duplicates of an active sibling).
- **AttemptV2 queue contract + durable removal WAL groundwork** (`Lidarr.Plugin.Common.HostBridge`, 16-commit hardening series) — opt-in `HostBridgeQueueStoreOptions { ContractVersion = AttemptV2 }` upgrades `HostBridgeDownloadTrackerStore<TItem>` from the legacy last-writer-wins dictionary to a versioned attempt-identity contract: every item carries `(DownloadId, AttemptId, Revision)` and mutations go through exact-CAS APIs (`TryAddAttempt`, `TryTransition` with a legal-transition state machine, `RemoveAttemptAsync` as a bounded two-phase cancel→stop-worker→remove transaction with a capped shutdown timeout). Persistence becomes a versioned envelope (`schemaVersion: 2`) with symmetric load/store limits (10k items / 16 MB / 32k-char strings), fail-closed handling of malformed or newer-schema state (the store starts empty and disables writes so the evidence file is never clobbered), and automatic migration of v1 array-format snapshots (deterministic dedupe, SHA-256-derived attempt IDs, evidence-driven restart recovery via `HostBridgeQueueStoreOptions.RestartEvidence`). `HostBridgeDownloadOrchestrator.StartTrackedDownloadV2Async` adds asynchronous pre-admission gating before queue commit, with rollback of the committed attempt when cancellation registration fails and an `OnBackgroundFault` observer for contained background failures. Physical data deletion under AttemptV2 is deliberately **neutralized** in this series (`deleteData: true` returns `QUEUE_REMOVAL_DEFERRED`, retaining the mapping and files) pending the durable removal coordinator; the supporting primitives ship now: `SafeOwnedRoot` (canonicalized, boundary-contained, link-rejecting staging root with a random owner marker and strict ACL/mode validation, revalidated on every operation), `RemovalWriterLease` (OS-level single-writer lock file), and `FileRemovalJournal` (schema-v1 crash-safe removal WAL: strict canonical JSON, full-record CAS, unique durable temps, atomic replace + parent-directory flush, four legal state/disposition edges, fail-closed scans). **LegacyV1 (the default) behavior is byte-for-byte unchanged**, including `Remove`'s `onDeleteError` callback seam and the cross-attempt re-grab guard — pinned by dedicated parity tests. ~4,300 lines of new tests cover identity/CAS, persistence v1→v2 migration and recovery, pre-admission, removal neutralization, and journal durability (crash-fault injection via hooks).
- **Dependency-CVE gate** (`scripts/ci/check-vulnerable-packages.ps1`, audit A-5) — new self-tested CI gate that runs `dotnet list package --vulnerable --include-transitive --format json` and fails on High/Critical findings (direct or transitive; threshold configurable via `-FailOnSeverity`; fail-closed on unparseable output, unknown severity strings, or a failing `dotnet list` invocation). Wired as a `dependency-scan` job in Common's Gitea CI and the guarded GitHub mirror. Plugins adopt by calling the script from their pinned submodule (`pwsh ext/Lidarr.Plugin.Common/scripts/ci/check-vulnerable-packages.ps1 -Target <sln|csproj>`). Its first live run immediately caught a real High finding — see Fixed below.
- **Cancellation + timeout on the OAuth refresh path** (`OAuthStreamingAuthenticationService`, audit C-4/C-5) — `RefreshTokensAsync` gains a `CancellationToken` overload (interface default forwards to the legacy method, so existing implementors keep compiling) and every refresh attempt is now bounded by a new `protected virtual TimeSpan RefreshTimeout` (default 100 s): a hung auth server fails the in-flight refresh with `TimeoutException` instead of wedging every token consumer for the process lifetime — even for legacy single-arg `RefreshTokensInternalAsync` overrides that ignore cancellation (the wait is bounded externally via `Task.WaitAsync`). A new `protected virtual RefreshTokensInternalAsync(refreshToken, cancellationToken)` overload lets services forward the timeout token into their HTTP call. Semantics pinned by tests: a timeout does **not** clear the cached session (a slow auth server is not a revoked token — the forced-relogin bug class), provider failures still do; caller cancellation abandons only that caller's wait and never aborts the shared single-flight refresh (refresh tokens are single-use on rotating providers), with the in-flight promise now cleared when the work completes rather than when the initiating caller stops waiting.
- **`SimpleDownloadOrchestrator` naming + payload-validation seams** — two new `protected virtual` extension points let a plugin reuse the shared album loop instead of overriding all of `DownloadAlbumAsync`: `BuildTrackOutputPath(outputDirectory, track)` (default: the historical `FileSystemUtilities.CreateTrackFileName` naming) and `ValidateDownloadedPayload(filePath, track)` (default: no-op; runs after post-processing/tagging with the final path on every track download — album loop and direct, URL and stream-provider engines; a throw deletes the rejected file and records the track as failed, feeding the AlbumCompletionPolicy incomplete⇒Failed contract). Lets qobuzarr's `QobuzDownloadOrchestrator` shrink to two small overrides (multi-disc `TrackFileNameBuilder` naming + magic-byte payload validation) over the shared loop. The naming seam is containment-enforced: the album loops validate the override's return through `PathTraversalGuard.IsPathWithinRoot(path, outputDirectory)` before the download engine touches it, so an override returning a path that escapes the album root fails ONLY that track (nothing is written outside the root, sibling tracks continue, and AlbumCompletionPolicy fails the incomplete album as usual) instead of writing anywhere the process can reach. Nested subpaths (`Disc 01/01 - Title.flac`) and lexically-wandering-but-inside-root paths remain allowed; the guard is lexical per `PathTraversalGuard`'s threat model (resolves `.`/`..`, not symlinks/junctions).
- **`AlbumSizeEstimator`** (`Lidarr.Plugin.Common.HostBridge`) — shared release-size estimator: `bytes = durationSeconds × (bitrate ÷ 8)` with an optional floor, plus a duration-fallback ladder (album duration → summed track durations → count × average). Lifts the duplicated `EstimateAlbumSize` (tidalarr) / `QualitySizeCalculator` (qobuzarr) logic into one place. Bitrate is bits-per-second as a `double` so non-round per-quality bitrates (e.g. Qobuz FLAC ≈ 1 411 200 bps) aren't truncated, and double arithmetic avoids the `int × int` overflow a kbps integer formula hits on long hi-res albums.
- **`MultiQualityReleaseBuilder` / `MultiQualityRelease`** (`Lidarr.Plugin.Common.HostBridge`) — emits one release per available quality for an album by composing `AlbumReleaseInfoBuilder` (per-tier GUID/URL/title) and `AlbumSizeEstimator` (per-tier size). Consolidates the "offer all quality tiers" indexer pattern (tidalarr `ConvertToReleaseInfosStatic`, qobuzarr's per-quality parser loop). Common cannot reference the host `ReleaseInfo` type, so `Build()` returns neutral `MultiQualityRelease` rows the plugin maps onto its own release object.
- **`HeuristicQueryOptimizer`** (`Lidarr.Plugin.Common.Services.Intelligence`) — rule-based, dependency-free implementation of `IQueryOptimizer` (artist/album feature extraction + a linear heuristic core), lighting up the previously dead-wired query-optimization seam without an ML dependency (#611).
- **`HlsManifestParser`** (`Lidarr.Plugin.Common.Services.Streaming.Manifests`) — spec-correct HLS (RFC 8216 / `.m3u8`) playlist parser, shared across plugins, alongside the existing `DashManifestParser` on the manifest-parser seam (#581).
- **`CencSampleDecryptor`** (`Lidarr.Plugin.Common.Services.Drm`) — shared CENC (Common Encryption, ISO/IEC 23001-7) sample decryptor for `cenc` AES-128-CTR protected samples; TDD'd against NIST AES-CTR vectors (#601).
- **`TriageReasonCodes.ConfidenceNotProvided`** (`"CONFIDENCE_NOT_PROVIDED"`) — new shared triage reason code (#567).
- **`CappedSearchChain`** (`Lidarr.Plugin.Common.Services.Intelligence`) — reusable static helper that builds the final ordered query list for capping plugins: takes at most `maxOverSpecific` of the best-first combined/album-only candidates, then unconditionally appends the artist-only catalogue fallback (never subject to the cap). The fallback-survival guarantee is the fix for the shipped "Bleu Jeans Bleu / Record n°V returned 0 results" bug, where a per-plugin `Take(N)` truncated away the artist-only tier. Promoting the pattern here means any capping plugin inherits the guarantee with cross-plugin test coverage rather than re-deriving (and re-breaking) it. Currently used by qobuz; `SearchRequestChainComplianceTestBase` provides the compliance guard that enforces the guarantee at adoption time.
- **`SearchRequestChainComplianceTestBase`** (`Lidarr.Plugin.Common.TestKit.Compliance`) — stronger search-provenance axis that drives a plugin's REAL `GetSearchRequests` and decodes each request through the `PlaceholderSearchUri` seam. Unlike `SearchTermProvenanceComplianceTestBase` (whose inputs are plugin-provided and can be hand-reconstructed, and which only checks issued ⊆ planned), this base is host-free, deterministic, and asserts the chain *preserves every* `BuildPlan` variant incl. the full artist-only fallback tier (catches the `Take(3)`/Bleu-Jeans-zero-results drop) and is sanitized for special characters. Now also supports opt-in exact-sequence enforcement (`RequiresExactPlanSequence`) for full-chain adopters (tidal/amazon/apple): catches duplicate emitted variants and reordering of variants after the first request — both invisible to the default set-based presence checks and both detectable by stop-policy regressions. Plugins should adopt it alongside the existing provenance axis.
- **`IAudioArtworkEmbedder` / `TagLibAudioArtworkEmbedder`** (`Lidarr.Plugin.Common.Services.Metadata`) + **`LyricsEnricher` tag-embedding** — downloaded albums previously arrived with no cover art and no lyrics: plugins shipped only the audio (no embedded PICTURE), and lyrics were `.lrc` sidecars that Lidarr's default `importExtraFiles=false` drops at import. The orchestrator now embeds the album cover as a FLAC PICTURE / MP3 APIC frame and the fetched lyrics into `Tag.Lyrics` (LRC `[mm:ss]` timestamps stripped so players show clean text), so both survive Lidarr import. `SimpleDownloadOrchestrator.ApplyArtworkAsync` resolves `track.Album.GetBestCoverArtUrl()`, then fetches through `RemoteMediaUriGuard.Strict` (SSRF: https-only, public destinations), an image-only Content-Type allowlist + magic-byte validation (rejects HTML soft-404s), and a bounded 10 MB read with a stall timeout before embedding. Best-effort throughout: a fetch/embed failure never fails the download; caller cancellation still propagates. Live-proven on qobuz (13/13 covers + lyrics embedded end-to-end).
- **`CoverArtEmbeddingComplianceTestBase`** (`Lidarr.Plugin.Common.TestKit.Compliance`) — cross-plugin parity axis proving an orchestrator plugin's download-path `StreamingAlbum` exposes a FETCHABLE cover URL via `GetBestCoverArtUrl()` (the predicate delegates to the same `RemoteMediaUriGuard.Strict` the orchestrator applies, so a URL that passes the test is exactly one the orchestrator will embed). Catches the two shipped failure modes: empty `CoverArtUrls` (art-less downloads) and a raw provider id in place of a URL (the tidal `CoverArtId` bug). Adopted by qobuz/tidal/amazon; apple is a non-adopter by design (its DRM/SDK download path bypasses the orchestrator cover seam). The ecosystem CI-contract enforces adoption.

### Changed
- **packaging-gates: canonical-abstractions sidecar is now opt-in (#549).** The packaging closure no longer forces the canonical `Lidarr.Plugin.Abstractions.dll` sidecar by default; plugins opt in explicitly. Removes a packaging-gate conflict for plugins that internalize abstractions via ILRepack.
- **local-ci: accept `includedFrameworks` in the .NET 8 runtime guardrail (#548).** The host-assembly extraction guardrail now tolerates the `includedFrameworks` shape so the .NET 8 / FluentValidation 9.5.4 check passes on current plugins-branch images.
- **`SearchStopPolicy` no longer has an accidental accumulate-all default (audit #8).** Added `Unknown = 0` (the uninitialized default) with explicit values (`AccumulateAll = 1`, `StopAfterFirstTierWithResults = 2`, `StopAfterFirstVariantWithResults = 3`), and `SearchPlanExecutor.ExecuteAsync` now throws `ArgumentOutOfRangeException` on `Unknown` or any undefined value instead of silently falling through to accumulate-all. **Breaking (numeric only):** `AccumulateAll` moved `0 → 1`; callers that pass the enum member by name (all ecosystem plugins) are unaffected on recompile — only code that persisted/relied on the raw integer `0` needs migration. Pinned by new `SearchPlanExecutorTests`.

### Removed
- **`TokenDelegatingHandler` and `HostConcurrencyGate` deleted as dead code.** Both had zero test references AND zero production references — not in this repo (src/testkit/templates/examples) and not in any of the five consuming plugins (tidalarr, qobuzarr, applemusicarr, amazonmusicarr, brainarr). `OAuthDelegatingHandler` covers the bearer-injection use case (with single-flight refresh on 401); `AdaptiveConcurrencyManager` / the HostBridge gates cover throttling. Deleting beats pinning tests onto a surface nobody consumes.
- **Public API analyzer + checked-in baselines retired.** Removed the `Microsoft.CodeAnalysis.PublicApiAnalyzers` PackageReference (RS0016/RS0017/RS0025/RS0026), the `src/**/PublicAPI/*.txt` baselines, the `PreparePublicApiBaselines` / `VerifyPublicApiAdditionalFiles` MSBuild targets, and the orphaned `tools/Update-PublicApiBaselines*.ps1` helpers. Common is ILRepack-internalized into each plugin, so a checked-in public-API baseline guarded no real consumer contract while adding review churn; public-surface changes are now tracked via SemVer + this CHANGELOG, with the merged build's packaging-closure check (`ValidatePackageClosure`) remaining the host-cleanliness gate. Docs that still referenced the analyzer/helper (`AGENTS.md`, `.github/pull_request_template.md`, `docs/reference/PUBLIC_API_BASELINES.md`, `CI.md`, `RELEASE_POLICY.md`, `TESTING_DOCS.md`, `FAQ_FOR_PLUGIN_AUTHORS.md`) were corrected, and a new `scripts/lint-doc-script-refs.ps1` docval gate (wired into CI) fails the build on any doc that points at a `tools/`-or-`scripts/`-rooted helper script that no longer exists.

### Fixed
- **`LiveDashboard` crashed on every Downloading row (malformed Spectre markup).** `Table.AddRow(params string[])` parses each cell as Spectre markup eagerly (even when the layout is never rendered), so the unescaped progress cell `"[67/100]"` threw `InvalidOperationException` for any item with `DownloadStatus.Downloading` — the actual root cause behind the 2026-02-04 "flaky on Linux CI" quarantines (Issue #318). The brackets are now escaped (`[[..]]` renders literal `[..]`). Found the moment the new CLI test lane first ran the previously-dead tests.
- **`LiveDashboard.RefreshIntervalMs = 0` hung the process.** `Task.Delay(0)` completes synchronously, and with the headless render path also synchronous, the refresh loop degenerated into a synchronous hot spin on the `StartAsync` caller's thread — `StartAsync` never returned (`RefreshIntervalMs_SetToZero_HandlesGracefully` deterministically hung the testhost). The delay is now clamped to a 1 ms floor so the loop always yields and stays cancellation-responsive.
- **CVE-2026-33116 / CVE-2026-26171 (both High): patched transitive `System.Security.Cryptography.Xml` 8.0.2 → 8.0.3** via a direct floor `PackageReference` (pulled in by `Microsoft.AspNetCore.DataProtection` 8.0.25; caught by the new dependency-CVE gate on its first run). Stays on the 8.0.x line so the merged plugin DLL's AssemblyRef keeps matching the Lidarr host ALC (same rationale as the `ProtectedData` pin). Remove the floor once DataProtection depends on >= 8.0.3 itself.
- **`SimpleDownloadOrchestrator` no longer loops a stale resume into HTTP 416 track failure.** When a preserved `.partial` made the URL engine send a `Range` the server could no longer satisfy, the resulting 416 (`RequestedRangeNotSatisfiable`) was classified transient and retried with the SAME stale partial — which can only 416 again — until `MaxDownloadAttempts` was exhausted, failing the track, then the album, then re-entering the host re-grab loop that retry-with-resume exists to prevent. A 416 on a resume attempt now discards the stale `.partial` + `resume.json` and restarts with a clean full GET (no `Range` header); the restart still consumes an attempt so a pathological always-416 server stays bounded by the attempt budget. A 416 when no partial exists (no `Range` sent) remains an ordinary bounded HTTP failure. Regression coverage: `SimpleDownloadOrchestratorRangeNotSatisfiableTests`.
- **`LrclibClient` fuzzy `/api/search` fallback now verifies the ARTIST, not just the title.** Candidates were filtered by normalized track title only (`artistName` was discarded from the search result), and the unknown-duration (`durationSeconds <= 0`) shortcut took the FIRST title match unconditionally — so a same-title row from a different artist (covers, common titles) could get its synced lyrics embedded into the wrong track. The candidate's `artistName` must now equal the requested artist under the same alnum-lowercase normalization as the title compare (so `AC/DC` vs `ACDC` still match); take-first/closest-duration selection applies only among artist-matching rows, and a row without a verifiable `artistName` is rejected. Duration-tolerance logic is unchanged. Regression coverage: `LrclibClientSearchFallbackTests`.
- **`RemoteMediaUriGuard` no longer permanently fails a download on a transient DNS-resolution failure (SSRF guard).** `Validate` returned the same `UriGuardResult.Blocked(...)` for a DNS-resolution FAILURE (the resolver threw, or returned no addresses) as for a genuine security rejection, and every download caller throws a *permanent* `InvalidOperationException` for any block — so a transient DNS blip permanently failed the track with no retry. This broke **every** Tidal download on the live instance: the Tidal audio-CDN host (`sp-ad-cf.audio.tidal.com`) intermittently failed to resolve from inside the container, and each failure produced `Refusing media request to an unsafe URL` / `Refusing to download chunk 0 from an unsafe URL: URL host could not be resolved` with no re-attempt (0/5 grabs completed, blocklist never fired). **Fix:** `UriGuardResult` gains an `IsTransient` marker + a `UriGuardResult.Transient(reason)` factory. `Validate` returns `Transient` **only** for the two resolution-FAILURE paths; every hard security block — non-https scheme, userinfo, cloud-metadata host, allowed-suffix miss, literal private IP, and (critically) a host that *resolves* to a private/loopback/link-local/reserved address — stays a hard `Blocked` (`IsTransient == false`). The SSRF guarantee is unchanged: the transient relaxation applies **only** where resolution itself failed and safety could not be confirmed, never where the destination resolved to something unsafe. Callers (`ChunkedHttpAssembler`, `HttpFileDownloadService`, all three `MediaRedirectSafeSender` validate sites, and `SimpleDownloadOrchestrator` — whose stream-URL guard moved inside the retry loop so a transient re-validates instead of connecting to an unconfirmed host) now throw a **retryable** `HttpRequestException` on a transient result (which Lidarr and the plugins' `IsTransientDownloadException`/retry logic already treat as retryable) and keep the permanent `InvalidOperationException` only for a hard block. The retry paths are all bounded, so a persistent DNS outage yields N attempts and then a normal network-error failure. Regression coverage: `RemoteMediaUriGuardTransientTests` (guard-level: resolver-throws/empty ⇒ transient; resolved-to-private / literal-private / non-https / metadata ⇒ hard block, not transient) and `SsrfTransientCallerTests` (each caller: transient ⇒ `HttpRequestException`, resolved-to-private ⇒ `InvalidOperationException`).
- **`BoundedConcurrentDictionary` no longer evicts when duplicate/update operations target an existing key at capacity.** The previous capacity check ran before determining whether `TryAdd`, `AddOrUpdate`, `GetOrAdd`, or the indexer setter were inserting a new key, so normal duplicate/update calls could clear the entire dictionary and then re-add/update one entry. Regression coverage now pins at-capacity duplicate/update semantics for all write surfaces.
- **`SimpleDownloadOrchestrator` distinguishes caller cancellation from provider/internal timeouts.** Caller cancellation still propagates from provider, post-processing, and metadata paths; non-caller provider or transport timeouts now become retried/failed track results instead of whole-album cancellation, and non-caller post-processing/metadata timeouts remain best-effort so an already-downloaded track is not discarded.
- **Ecosystem CI-contract now verifies GitHub mirror workflows are equivalent when declared.** Plugins with `mirrorWorkflows > 0` must provide exactly one GitHub CI mirror workflow and wire the same core expectations as Gitea: shared Common lint runner, `verify-local.ps1`, Common pin verification, and Gitleaks secret scanning. Repos with no declared mirror workflows remain explicitly Gitea-only.
- **Ecosystem CI-contract hardens ALC/pin convergence as a fail-by-default gate.** Common's Gitea workflow now runs a live five-repo `ecosystem-contract` job on `main`, manual dispatch, and schedule; `verify-ecosystem-ci-contract.ps1 -CI` now fails on missing manifest repos, missing plugin `verify-local.ps1` wiring, and cross-plugin Common SHA divergence instead of treating drift as advisory.
- **`BoundedConcurrentDictionary` keeps its settled capacity bound under concurrent `GetOrAdd` misses.** The previous pre-insert eviction check let many unique-key factories pass at `capacity - 1` and then all insert, leaving the dictionary above its advertised cap until a later mutation happened to clear it. Capacity-checked mutations are now serialized at insertion, and `GetOrAdd` computes miss values outside the mutation lock before rechecking under the lock. Regression coverage pins the concurrent near-capacity miss case.
- **Analyzer NuGet package now ships the assembly under `analyzers/dotnet/cs` (audit #4).** `tools/Analyzers/Lidarr.Plugin.Analyzers` previously packed with `IncludeBuildOutput=true` and no analyzer PackagePath, so `dotnet pack` placed the DLL under `lib/` — NuGet treated it as a runtime reference and consuming plugins silently got NO analyzer despite the rules being unit-tested. Set `IncludeBuildOutput=false` + `SuppressDependenciesWhenPacking` and pack the DLL to `analyzers/dotnet/cs`. Guarded by `tests/Packaging/AnalyzerPackagingTests.cs`, which packs the real project and inspects the `.nupkg` layout.
- **Source hygiene: removed raw NUL bytes from two test sources (audit #13).** Replaced literal `0x00` (and a `0x00 0x01 0x02` run) inside string literals with C# escapes. New `tests/Hygiene/SourceHygieneTests.cs` fails by default if any tracked text source contains a NUL byte (the repo-wide guard caught a second offender the audit had missed).
- **`PathTraversalGuard.IsDescendant` — accept children when the configured root has a trailing separator (#552).** `Path.GetFullPath(root)` preserves a trailing directory separator, so the subsequent prefix check compared the child against `"<root>/"` and a doubled separator made *every* legitimate descendant fail. The guard now trims trailing `Path.DirectorySeparatorChar` / `AltDirectorySeparatorChar` off the canonical root before comparing. **Downstream impact**: this was the root cause of tidalarr/qobuzarr rejecting *all* downloads with "resolves outside the download directory" whenever the user's download path ended in a slash. Regression tests cover trailing-separator and bare-root cases (`tests/HostBridge/PathTraversalGuardTests.cs`).

### Deprecations
- **`IAuthFailureGateRegistry` / `AuthFailureGateRegistry`** — deprecated in favour of a direct `ConcurrentDictionary<string, AuthFailureGate>` per-plugin. A Wave-26 adversarial audit found zero non-test plugin consumers across all four ecosystem repos; every real call-site builds its own gate map so it can pair a custom `IAuthFailureHandler` (e.g. `SlidingWindowAuthFailureHandler`) with each gate — something the registry cannot do because it hard-wires `DefaultAuthFailureHandler` internally. Both the interface and the concrete class are marked `[Obsolete(error: false)]`. They will be removed in v2.0.0.

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.17.0...v1.18.0)

## [1.17.0] - 2026-05-25
**Upgrade note:** Wave 21 parity helpers for plugin ecosystem consistency.

### Added
- Unified plugin version-bump helper (Wave 18C)
- Edition/Explicit/Live bracket slots to AlbumReleaseInfoBuilder
- AlbumDownloadUri parser + builder (Wave 19B)
- PathTraversalGuard.ContainsTraversalAttempt probe

### Fixed
- Secrets context not allowed in if expressions across 4 workflow files

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.16.0...v1.17.0)

## [1.16.0] - 2026-05-25
**Upgrade note:** Adds SlidingWindowAuthFailureHandler, observability helpers, and ecosystem parity matrix.

### Added
- SlidingWindowAuthFailureHandler — K-of-N-in-W circuit semantics
- Scrub.UrlAndStripQuery (defensive query-strip sibling)
- Ecosystem parity matrix — single source of truth across 5 repos

### Fixed
- Secrets context not allowed in if expressions across 4 files

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.15.0...v1.16.0)

## [1.15.0] - 2026-05-25
**Upgrade note:** BoundedConcurrentDictionary API extension with packaging and test improvements.

### Added
- BoundedConcurrentDictionary API extension

### Fixed
- ecosystem-parity-lint falls back to Directory.Build.props for <Version>
- PackageClosure discovers plugin repos via env or sibling walk
- Nightly Run tests step uses bash so '\' line continuation works on Windows
- local-ci.ps1 initializes $resolvedFlags so empty BuildFlags doesn't trip strict-mode
- PackageClosure plugin tests use [SkippableTheory] so missing builds skip
- TokenProtectorFactory degrades on SecretService 'not available' (Wave 17O)
- local-ci .NET 8 guardrail tolerates includedFrameworks shape

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.14.2...v1.15.0)

## [1.14.2] - 2026-05-24
**Upgrade note:** OAuth single-flight refresh fix.

### Fixed
- OAuth single-flight RefreshTokensAsync via promise sharing

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.14.1...v1.14.2)

## [1.14.1] - 2026-05-24
**Upgrade note:** SimpleDownloadOrchestrator OCE rethrow fix.

### Fixed
- SimpleDownloadOrchestrator rethrows OCE from stream-provider path

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.14.0...v1.14.1)

## [1.14.0] - 2026-05-24
**Upgrade note:** Removes deprecated AdaptiveRateLimiter family and WithExecutor (Wave 17K) plus test coverage improvements.

### Removed
- Deprecated AdaptiveRateLimiter family (removed in Wave 17K)
- WithExecutor method (removed in Wave 17K)

### Added
- Cancellation, concurrency boundary, and backpressure test coverage (Wave 11C audit)
- OAuthStreamingAuthenticationService Wave 11C edge-case coverage

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.13.1...v1.14.0)

## [1.13.1] - 2026-05-24
**Upgrade note:** PluginPackaging absolute-path fix.

### Fixed
- PluginPackaging absolute LidarrAssembliesPath no longer double-prefixed

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.13.0...v1.13.1)

## [1.13.0] - 2026-05-24
**Upgrade note:** Scrub.Url unification plus PathTraversalGuard hardening.

### Added
- PathTraversalGuard hardening from adversarial review (Wave 17F)

### Changed
- Scrub.Url delegates recognition to LogRedactor (Wave 17F)

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.12.0...v1.13.0)

## [1.12.0] - 2026-05-24
**Upgrade note:** StreamingApiRequestBuilder fail-on-reuse guard plus urgent PathTraversalGuard fix.

### Added
- StreamingApiRequestBuilder seals after Build() to prevent query bleed
- lint-sync-over-async accepts -SrcDir for non-standard layouts (e.g., Brainarr)

### Fixed
- PathTraversalGuard rejected valid descendants when root had trailing separator (URGENT)

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.11.0...v1.12.0)

## [1.11.0] - 2026-05-24
**Upgrade note:** Host-bridge orchestration primitives and resilience improvements.

### Added
- HostBridgeDownloadOrchestrator — settings-snapshot + tracked enqueue (lift wave A item 2)
- RetryPolicyOptions.ForLocalProviders preset (100ms/2s, 3 attempts)
- AlbumReleaseInfoBuilder — unified ReleaseInfo string construction (lift wave A item 8)
- CONSUMING.md cross-plugin helper guide

### Fixed
- Removed stale paramref in HostBridgeDownloadOrchestrator class-level doc
- Verify-tag-matches-version to check Directory.Build.props

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.10.0...v1.11.0)

## [1.10.0] - 2026-05-24
**Upgrade note:** Host-bridge, resilience, and observability primitives plus adversarial-review fixes.

### Added
- PluginLifecycle.Shutdown — static teardown hook registry
- BoundedConcurrentDictionary<TKey,TValue> — clear-on-overflow capped dict
- HostGateRegistry.Shutdown() — release timer on plugin unload
- HostBridgeRuntimeCache — generic gated runtime cache (lift wave D item 6)
- Scrub helpers for secret-redaction in logs and URLs
- PluginLogContext — pluginName/correlationId/provider/operation scope
- BackendHealthCache — 30s grace cache for known-down backends

### Fixed
- Path-traversal guard + remaining-time rounding (adversarial review)
- MultiPluginAlcTests Skip.If calls use [SkippableFact] instead of [Fact]
- XML cref refs qualified for -warnaserror CI
- Reduced WarnOnce concurrent-stress thread count for CI thread-pool

### Changed
- Single-source <Version> across Common + Abstractions csprojs
- Host-bridge types hardening from adversarial review

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.9.5...v1.10.0)

## [1.9.5] - 2026-05-23
**Upgrade note:** Host-bridge primitives plus WarnOnce, TestValidationBuilder, JsonFileStore, and configuration fixes.

### Added
- TestValidationBuilder — accumulate-then-build pattern for plugin Test() pipelines
- WarnOnce helper for warn-then-debug log gating
- PlaceholderSearchUri — unify search-query placeholder URIs (lift wave A item 5)
- HostBridgeDownloadTracker — unify per-download state (lift wave A item 1)
- PrefixedReleaseGuidParser — unify GUID/URL extraction (lift wave A item 3)
- JsonFileStore for file-based configuration storage

### Fixed
- Trim trailing slash from TargetDir before passing to pwsh -OutputDir
- ValidatePackageClosure delegates to .ps1 file (shell var-eat fix)
- WarnOnce concurrent tests use Task.WhenAll (xUnit1031)
- FileStreamingResponseCache + FileConditionalRequestState use PluginConfigRoots
- OS-aware case sensitivity in PathTraversalGuard
- Abstractions Version bumped to 1.9.5 to match release tag

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.9.4...v1.9.5)

## [1.9.4] - 2026-05-23
**Upgrade note:** Closes deferred adversarial-review findings F4 + F5 + F7 + F8.

### Added
- NullUniversalAdaptiveRateLimiter — single source of truth for plugin test stubs
- PathTraversalGuard — defense-in-depth for plugin download paths

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.9.3...v1.9.4)

## [1.9.3] - 2026-05-23
**Upgrade note:** Adversarial-review hardening of v1.9.2: F1+F2+F3+F6 fixes.

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.9.2...v1.9.3)

## [1.9.2] - 2026-05-23
**Upgrade note:** Lidarr-Docker token-protection startup fix.

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.9.1...v1.9.2)

## [1.9.1] - 2026-05-23
**Upgrade note:** HttpExceptionClassifier for categorised connection-test failures.

### Added
- HttpExceptionClassifier — categorised connection-test failures (TDD)

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.9.0...v1.9.1)

## [1.9.0] - 2026-05-23
**Upgrade note:** AuthFailureGate surface, adversarial-review must-fixes, and TestKit lifts.

### Added
- AuthFailureGate surface + 5 adversarial-review must-fixes
- SecureMemory + Conservative rate-limit profile + PagedResponseValidator
- PluginVersionContract, PluginPackagingContract, PublishedReleaseInstallability lifted to TestKit
- Lidarr.Plugin.*.dll naming convention enforcement
- ValidatePackageClosure target rejects forbidden DLLs at build time
- Multi-plugin ALC coexistence + per-plugin package-closure tests
- Ecosystem version contract + version-contract enforcement

### Fixed
- Blocking-wait + flaky cache tests + version contract docs
- COM-005 path-validation hardening + COM-011 download integrity check
- Coexistence proof pointed at renamed applemusicarr DLL
- Forced single-threaded MSBuild in nightly to avoid Windows parallel-build file lock

### Changed
- Fixed blocking-wait inside lock with SemaphoreSlim + await Task.Delay in StreamingPluginMixins.StreamingIndexerMixin.ApplyRateLimitAsync
- Removed Task.Delay(...).Wait() inside lock pattern

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.8.0...v1.9.0)

## [1.8.0] - 2026-05-10
**Upgrade note:** Multi-plugin co-existence fix, parity-lint enforcement, async rate-limit refactor, and SmartCache test suite fixes.

### Added
- Ecosystem version contract (versionContract) to parity-spec.json
- forbiddenFields enforcement wired into parity-lint
- Isolated AssemblyLoadContext per plugin for multi-plugin co-existence

### Fixed
- Multi-plugin ALC co-existence prevents type-identity collisions
- PluginSandbox strict/permissive loader modes
- PluginType validation, thread-safe bridges, GetTypes restore, fixture caching
- IsHostBridgeBuild accepts merged builds (C1) + sidecar-tolerant scripts (C3)
- Microsoft.Extensions.* pinned to 8.0.x to match Lidarr host
- Lidarr.Plugin.Abstractions merged into plugin DLL (cross-ALC fix)
- Cross-ALC HttpClient metrics + Azure DataProtection
- Build flags propagated through local-ci restore/package/test stages
- Microsoft.Extensions.* >=9.0.0 blocked with wildcard
- Sync-over-async patterns eliminated and lint allowlist key fixed

### Changed
- StreamingPluginMixins.StreamingIndexerMixin.ApplyRateLimitAsync replaced Task.Delay(...).Wait() with SemaphoreSlim + await Task.Delay
- SmartCache tests: removed Skip from TryGet_ReturnsFalseForExpiredItem and Eviction_LowPriorityItemsEvictedFirst with TimeProvider injection

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.7.1...v1.8.0)

## [1.7.1] - 2026-03-27
**Upgrade note:** Patch release hardening bridge defaults, sandbox resilience, and test coverage.

### Added
- DefaultDownloadStatusReporter; AddBridgeDefaults() now registers 4 reporters
- 74 new tests: MemoryHealthMonitor, StreamingApiRequestBuilder, rate limit edge cases

### Fixed
- PluginSandbox hardening: ReflectionTypeLoadException handling, single IPlugin enforcement, PluginType option, DefaultHostVersion updated to 3.1.2.4913
- Thread-safe bridge singletons (volatile + lock pattern)
- Compliance test rewrite with fixture-backed BridgeComplianceTests
- Guard patterns (ArgumentNullException.ThrowIfNull) throughout bridge layer
- GetTypes restored (from GetExportedTypes) for broader type discovery

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.7.0...v1.7.1)

## [1.7.0] - 2026-03-26
**Upgrade note:** Bridge contracts shipped with 8 new Abstractions interfaces and default implementations.

### Added
- Bridge contract interfaces: IAuthFailureHandler, IIndexerStatusReporter, IRateLimitReporter, IDownloadStatusReporter, IIndexerRequestBuilder, IIndexerResponseParser<T>, IIndexerWithMetadata, IRssFeedProvider
- Default implementations: DefaultAuthFailureHandler, DefaultIndexerStatusReporter, DefaultRateLimitReporter
- DI extension: AddBridgeDefaults() registers all defaults via TryAddSingleton
- Fixture-backed compliance tests: BridgeComplianceTests (15 behavioral) + BridgeDefaultsActivationTests (4 DI activation)
- TestKit: PluginSandbox for isolated ALC plugin loading, BridgeComplianceFixture for bridge contract testing
- Behavioral docs: BRIDGE_RUNTIME_CONTRACTS.md

### Changed
- CliWrap 3.10.0 -> 3.10.1
- coverlet.collector 8.0.0 -> 8.0.1
- DataProtection 8.0.x -> 8.0.25
- azure/login v2 -> v3
- release-drafter v6 -> v7

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.6.0...v1.7.0)

## [1.6.0] - 2026-03-11
**Upgrade note:** Major infrastructure release with ecosystem parity testing, sync-over-async cleanup, diagnostics namespace, and dependency updates.

### Added
- Ecosystem parity test infrastructure (lint + TestKit base class)
- Sync-over-async pattern elimination with lint enforcement
- Diagnostics namespace for non-LLM providers
- Packaging gates improvements (contents manifest, canonical Abstractions)
- Local CI runner (local-ci.ps1) for offline verification
- 6-Month Autonomous Development Roadmap (Phases 12-23)
- SHA pin enforcement, contents manifest gate, warning budget visibility
- Canonical reason codes, diagnostic error codes
- Cross-platform path validation
- Comprehensive documentation (roadmap phases 12-23, KPI definitions, phase gate evidence)

### Fixed
- Sync-over-async patterns in StreamingTokenManager.ClearSession()
- Windows CI credential issue in change detection step
- Local CI Docker exit codes capture
- ManifestCheck StrictMode null-safe access for optional manifest targets
- PluginPack cached path temp dir cleanup race condition
- Reserved device name normalization for cross-platform filesystem safety

### Changed
- Microsoft.CodeAnalysis.CSharp 4.10.0 -> 4.14.0
- Microsoft.Extensions.Configuration.Json 8.0.0 -> 8.0.1
- Microsoft.Extensions.TimeProvider.Testing 8.5.0 -> 9.10.0
- System.Security.Cryptography.ProtectedData 8.0.0 -> 9.0.14
- coverlet.collector 6.0.4 -> 8.0.0
- xunit 2.9.2 -> 2.9.3
- Spectre.Console 0.50.0 -> 0.54.0
- Microsoft.NET.Test.Sdk 17.11.1 -> 18.3.0
- Microsoft.Extensions.Logging.Abstractions 8.0.1 -> 8.0.3
- JsonSchema.Net 7.2.3 -> 7.4.0
- actions/download-artifact v4 -> v8, actions/upload-artifact v4 -> v7

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.5.1...v1.6.0)

## [1.5.1] - 2026-01-17
**Upgrade note:** Verification merge-train improvements and test coverage.

### Added
- verify-merge-train scripts
- -SkipIntegration switch for test filtering
- lidarr-taglib NuGet source for Docker builds
- Comprehensive filename/path contract tests

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.5.0...v1.5.1)

## [1.5.0] - 2026-01-17
**Upgrade note:** E2E infrastructure, string protection facade, advanced circuit breaker, and extensive CI improvements.

### Added
- Publish canonical Abstractions DLL as release asset
- NuGet.org publishing for Abstractions
- IStringProtector facade for security
- AdvancedCircuitBreaker for Brainarr parity
- TimeProvider injection to CircuitBreaker for deterministic testing
- E2E contract invariant tests
- e2e-host-versions.psm1 module for host version compatibility checks
- E2E infrastructure: tripwire workflow self-test, sources hermetic test, multiPluginMode
- Parity-lint for detecting code re-inventions
- CI Gate workflow for docs-only PR branch protection
- Build & Test always report status (skip steps on docs-only PRs)
- Change detection self-test to prevent regression
- E2E error codes: E2E_CONFIG_INVALID, E2E_DOCKER_UNAVAILABLE, E2E_IMPORT_FAILED, E2E_API_TIMEOUT, E2E_QUEUE_NOT_FOUND, E2E_ZERO_AUDIO_FILES
- Structured details for E2E_NO_RELEASES_ATTRIBUTED, E2E_METADATA_MISSING, E2E_PROVIDER_UNAVAILABLE, E2E_INTERNAL_ERROR
- E2E_CONFIG_INVALID helper and tests
- E2E preflight: Lidarr API unreachable detection and retry logic
- Shared deterministic release selection helper
- Host ALC fix detection to manifest
- Stable sort key to PostRestartGrab release selection
- SampleFile guarantee for all E2E_METADATA_MISSING paths

### Fixed
- Request log URLs query-safe
- Multi-plugin smoke test canary gating
- StreamingTokenManager deterministically testable with TimeProvider
- TestKit AdditionalProperties to prevent CS2012 file locks
- E2E schema gate matches implementation field
- Grab gate SelectionBasis + explicit internal error
- E2E_CONFIG_INVALID wired to Configure gate failure sites
- E2E preflight auth detection and retry logic
- E2E cap foundIndexerNames and emit attribution details
- E2E stop PowerShell parse error
- CI: normalize ./ prefix in change detection classifier
- CI: host override steps run
- ILRepack internalize exclude syntax corrected

### Changed
- Removed paid-down parity-lint baselines
- Error codes documentation sync tripwire

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.2.1...v1.5.0)

## [1.2.1] - 2025-10-11
**Upgrade note:** Security, packaging, and CI improvements.

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.2.0...v1.2.1)

## [1.2.0] - 2025-10-11
**Upgrade note:** Publish-packages workflow, observability helpers, and path validation.

### Added
- Publish-packages workflow for GitHub Packages
- Minimal ILogger/Activity helpers (draft)
- Shared observability events proposal
- PathValidation.IsReasonablePath for permissive CLI/plugin checks

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.1.7-rc.1...v1.2.0)

## [1.1.7-rc.1] - 2025-10-11
**Upgrade note:** Release candidate for v1.1.7.

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.1.7...v1.1.7-rc.1)

## [1.1.7] - 2025-10-11
**Upgrade note:** Policy-first HTTP path, safer dedup/caching defaults, clearer redirect semantics.

### Added
- Builder → Options → Executor integration: stamp endpoint/profile/params/scope
- GET singleflight dedup when cache misses with race-guarded recheck
- Query canonicalization for multivalue params
- 307/308 auto-follow preserving method/body; 301/302/303 auto-follow for safe methods only
- Cache sliding TTL coalesced with absolute expiration and stale-grace
- Conditional GET: ETag/Last-Modified persisted and revalidation path tested
- OTel quickstart (feature flag LPC_OTEL_ENABLE=1) + sample Grafana dashboard
- Template: dotnet new lidarr-plugin with minimal settings/module/indexer
- Analyzers (dev dependency): LPC0001 avoid raw HttpClient usage; LPC0002 prefer policy-based overload

### Changed
- Request deduplication cancels in-flight tasks on dispose with TrySet* guards
- README Maintainer Checklist references Lidarr setup script

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.1.6...v1.1.7)

## [1.1.6] - 2025-10-10
**Upgrade note:** Diagnostics and concurrency improvements.

### Added
- PluginOperationResultJson helper for consistent diagnostics
- 304 revalidation path with brief stale grace
- Windows file concurrency improvements in FileTokenStore

### Fixed
- Retry semantics: when honoring Retry-After absolute dates, do not add jitter
- CI: grant permissions for PR test result annotations

### Changed
- QueryCanonicalizer made public; DefaultProfiles constants added; PublicAPI baselines updated
- FileTokenStore writes use unique temp files with retry on replace/move
- CI: disable analyzers during build steps; dedicated PublicAPI drift steps retained

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.0.0...v1.1.6)

## [1.0.0] - 2025-08-26
**Upgrade note:** Initial release of Lidarr.Plugin.Common shared library.

### Added
- Initial shared library repository with base streaming infrastructure
- Base classes: BaseStreamingSettings, BaseStreamingIndexer<T>, BaseStreamingDownloadClient<T>, BaseStreamingAuthenticationService<T>
- Services: StreamingResponseCache, StreamingApiRequestBuilder, QualityMapper, PerformanceMonitor, StreamingPluginModule
- Models: StreamingArtist, StreamingAlbum, StreamingTrack, StreamingQuality, StreamingQualityTier
- Utilities: FileNameSanitizer, HttpClientExtensions, RetryUtilities
- Testing support: MockFactories, TestDataSets
- Interfaces: IStreamingAuthenticationService<T>, IStreamingResponseCache, IQueryOptimizer

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/082800c...v1.0.0)

---

## Version Management

- **1.x.x**: Backward-compatible API evolution.
- **0.x.x**: Development versions where breaking changes are allowed.
- **x.Y.x**: Feature additions (minor).
- **x.x.Z**: Bug fixes and patches (patch).

## Migration Guide

1. Check this changelog for breaking changes.
2. Update plugin project references.
3. Run provided migration scripts (if any).
4. Test thoroughly with the updated shared library.
5. Update plugin version numbers to match compatibility.

## Support

- **Issues**: Report bugs in the main Qobuzarr repository.
- **Feature Requests**: Discuss in GitHub Discussions.
- **Community**: Join the streaming plugin developer community.
- **Documentation**: See `README.md` and the `docs/` folder.
