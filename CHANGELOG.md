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

### Deprecations
- **`IAuthFailureGateRegistry` / `AuthFailureGateRegistry`** — deprecated in favour of a direct `ConcurrentDictionary<string, AuthFailureGate>` per-plugin. A Wave-26 adversarial audit found zero non-test plugin consumers across all four ecosystem repos; every real call-site builds its own gate map so it can pair a custom `IAuthFailureHandler` (e.g. `SlidingWindowAuthFailureHandler`) with each gate — something the registry cannot do because it hard-wires `DefaultAuthFailureHandler` internally. Both the interface and the concrete class are marked `[Obsolete(error: false)]`. They will be removed in v2.0.0.

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
- 307/308 auto-follow preserving method/body; 301/302 auto-follow for safe methods only
- Cache sliding TTL coalesced with absolute expiration and stale-grace
- Conditional GET: ETag/Last-Modified persisted and revalidation path tested
- OTel quickstart (feature flag LPC_OTEL_ENABLE=1) + sample Grafana dashboard
- Template: dotnet new lidarr-plugin with minimal settings/module/indexer
- Analyzers (dev dependency): LPC0001 avoid raw HttpClient usage; LPC0002 prefer policy-based overload

### Changed
- Request deduplication cancels in-flight tasks on dispose with TrySet* guards
- README Maintainer Checklist references Lidarr setup script

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.1.6...v1.7)

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
