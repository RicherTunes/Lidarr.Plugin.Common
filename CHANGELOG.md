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

## [1.12.0] - 2026-05-24
**Upgrade note:** Minor bump with one defensive behavior change. `StreamingApiRequestBuilder.Build()` now seals the instance; calling `Build()` again or any mutator after `Build()` throws `InvalidOperationException`. Plugins that already create a fresh builder per request (the documented pattern) need no changes. Plugins that stored a shared builder in a field will hit the throw — fix the call site to use a per-call factory. `BuildForLogging()` is read-only and does NOT seal.

**Highlights — defensive hardening**
- `StreamingApiRequestBuilder` is now single-use after `Build()`. Regression class caught in Tidalarr v1.2.7: a shared builder field accumulated query parameters across calls, so after `Test()` sent `query=test` every later search URL contained `query=test` as the first param and the API ignored the caller's actual query. The seal makes the misuse impossible to repeat silently — it fails fast at the throw instead of bleeding corrupted requests in production.

**Cleanup**
- Removed dead `_requestBuilder` field from `BaseStreamingIndexer` — declared and initialized but never read, never accessible to subclasses (private). The protected `CreateRequest` method already builds fresh per call (the correct pattern). Saves one allocation per indexer instance.

**Breaking changes:** None for plugins following the documented per-call factory pattern. Plugins that stored a shared builder will now see `InvalidOperationException` on the second call — that's the regression class this commit prevents from silently corrupting requests.
**Deprecations:** None
**Dependency changes:** None

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.11.0...v1.12.0)

## [1.11.0] - 2026-05-24
**Upgrade note:** Minor bump — two new host-bridge primitives and a resilience preset, all additive. Plugins bumping from v1.10.0 need no code changes but can now replace hand-rolled orchestration and retry logic with the new Common types.

**Highlights — host-bridge primitives**
- `AlbumReleaseInfoBuilder` — unified `ReleaseInfo` string construction (lift wave A item 8); eliminates per-plugin format divergence for release-info payloads sent to Lidarr.
- `HostBridgeDownloadOrchestrator` — settings-snapshot + tracked-enqueue orchestrator (lift wave A item 2); fixes a ProbeOnly race where an in-flight snapshot could observe partial settings writes.

**Highlights — resilience**
- `RetryPolicyOptions.ForLocalProviders` preset — 100 ms / 2 s backoff, 3 attempts, tuned for low-latency local/LAN providers; companion `RetryPolicyFactory.CreateForLocalProviders` for one-line wiring.

**Breaking changes:** None
**Deprecations:** None
**Dependency changes:** None

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.10.0...v1.11.0)

## [1.10.0] - 2026-05-24
**Upgrade note:** Minor bump — large batch of new primitives, all additive. Plugins bumping from v1.9.5 need no code changes but can now replace hand-rolled implementations with the lifted types.

**Highlights — host-bridge primitives**
- `HostBridgeRuntimeCache` — generic gated runtime cache (lift wave D item 6); replaces per-plugin hand-rolled TTL maps.

**Highlights — resilience primitives**
- `BackendHealthCache` lifted to Common — 30 s grace cache for known-down backends; eliminates hand-rolled per-plugin copies.
- `BoundedConcurrentDictionary<TKey,TValue>` — clear-on-overflow capped dict; bounds in-memory registries.
- `PluginLifecycle` — static teardown hook registry; plugins register cleanup once, Common calls them on shutdown.

**Highlights — observability primitives**
- `PluginLogContext` — pluginName / correlationId / provider / operation ambient scope for structured logs.
- `Scrub` helpers — secret-redaction for log strings and URLs.

**Highlights — hosting / lifecycle**
- `HostGateRegistry.Shutdown()` — releases timer on plugin unload; prevents timer-thread leaks in multi-plugin Lidarr hosts.

**Highlights — hardening**
- `PluginConfigRoots` path-traversal guard — defense-in-depth against operator-supplied path segments containing `..`.
- `BackendHealthCache` remaining-time rounding fix — sub-second display no longer shows negative values.

**Breaking changes:** None
**Deprecations:** None
**Dependency changes:** None

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.9.5...v1.10.0)

## [1.9.5] - 2026-05-23
**Upgrade note:** Adds six new host-bridge primitives and three helper/testkit additions from lift wave A. All changes are purely additive — plugins bumping from v1.9.4 do not need code changes, but can now replace hand-rolled implementations with the new Common types.

**Highlights — host-bridge primitives**
- `PathTraversalGuard` — defense-in-depth for plugin download paths; OS-aware case sensitivity.
- `HostBridgeDownloadTracker` — unified per-download state (lift wave A item 1).
- `PrefixedReleaseGuidParser` — unified GUID/URL extraction from prefixed release identifiers (lift wave A item 3).
- `PlaceholderSearchUri` — unified search-query placeholder URI construction (lift wave A item 5).

**Highlights — hosting / path fixes**
- `FileStreamingResponseCache` + `FileConditionalRequestState` now resolve storage paths via `PluginConfigRoots` instead of `SpecialFolder.ApplicationData` chains (fixes Docker/hotio path issues for file-caching consumers).

**Highlights — new helpers**
- `WarnOnce` (in `Services.Diagnostics`) — warn-then-debug log-gating latch; eliminates hand-rolled static `HashSet` guards across plugins.
- `TestValidationBuilder` (in `Services.Validation`) — accumulate-then-build pattern for plugin `Test()` pipelines; fixes latent multi-field failures where the first error silently swallowed subsequent ones.
- `JsonFileStore<TKey, TValue>` (in `Services.Storage`) — type-safe JSON-backed key-value store.
- `NullUniversalAdaptiveRateLimiter` (in `TestKit`) — single source of truth for plugin test stubs; no more per-plugin `NullRateLimiter` copies.

**Breaking changes:** None
**Deprecations:** None
**Dependency changes:** None

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.9.4...v1.9.5)

## [1.9.4] - 2026-05-23
**Upgrade note:** Closes the four deferred adversarial-review findings from the v1.9.2/v1.9.3 review (F4 + F5 + F7 + F8). All changes are additive; downstream plugins bumping from v1.9.3 do not need code changes.

**Highlights — F4/F5/F7/F8 fixes**

- **F4 (MED) — Write probe hardened.** Previously `EnsureKeysDirIsWritable` wrote a 0-byte probe file with `File.WriteAllBytes`, which (a) some POSIX overlay/sshfs filesystems happily accept while rejecting real content, (b) couldn't detect write-then-truncate corruption, (c) had a TOCTOU window between create and delete. Now: writes a non-empty `LPC-PROBE` payload via `FileMode.CreateNew + FileShare.None`, reads it back to verify the round-trip, and refuses well-known system paths (`/etc/`, `/proc/`, `/sys/`, `/dev/`, `/boot/`, `/usr/bin/`, `/bin/`, etc. on Linux; `\Windows\`, `\Windows\System32\`, `\ProgramData\Microsoft\Crypto\` on Windows) so an operator typo on `LP_COMMON_KEYS_PATH` can't scribble even a probe file into a critical system dir.
- **F5 (MED) — Candidate chain walks through unwritable entries.** v1.9.2/v1.9.3 used `GetDefaultKeysDir` which returned the FIRST rooted candidate. If that candidate was rooted but unwritable (e.g. a `/config:ro` bind-mount), the factory immediately degraded to `NullTokenProtector` instead of trying the next candidate. Now: factory iterates the full chain via new `EnumerateKeysDirCandidates`, write-probing each, and only degrades when every candidate fails. `LP_COMMON_KEYS_PATH` (operator override) is still honoured exclusively — silently re-routing an explicitly-set path would surprise operators.
- **F7 (LOW) — Windows prefers Roaming AppData.** On Windows, `ApplicationData` (Roaming, survives profile migration / domain roam) is now tried before `LocalApplicationData` (which does NOT survive a profile roam). DPAPI ciphertext encrypted with a Local-AppData key ring would have been silently undecryptable after a roam. DPAPI-user is still the default backend on Windows (so this mostly affects forced `LP_COMMON_PROTECTOR=dataprotection` mode), but the ordering matters when DataProtection IS used. On Linux/macOS, the order stays `LocalApplicationData` (~/.local/share, XDG-canonical for data) before `ApplicationData` (~/.config).
- **F8 (LOW) — Ongoing visibility into degradation.** v1.9.2/v1.9.3 only exposed degradation via the static `LastDiagnostics` snapshot, which plugin code had to read at startup. Now adds:
  - `TokenProtectorFactory.DegradationDetected` — static event that fires on every degradation transition. Subscribers can surface the warning to host log / metrics / health checks.
  - `TokenProtectorFactory.LogDegradationOnce(Action<string> logWarning)` — at-most-once-per-process helper for plugins to call from credential hot paths (`set_ApiKey`, settings save). Includes the actionable remediation hints (`LP_COMMON_KEYS_PATH`, `LP_COMMON_REQUIRE_PROTECTOR`).

**Breaking changes:** None
**Deprecations:** None
**Dependency changes:** None

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.9.3...v1.9.4)

## [1.9.3] - 2026-05-23
**Upgrade note:** Adversarial-review hardening of v1.9.2's token-protection fallback. Four defects identified by post-release review have been corrected; the v1.9.2 bug fix proper (the candidate chain in `GetDefaultKeysDir`) is unchanged. Downstream plugins bumping from v1.9.2 to v1.9.3 do not need code changes — the API surface is wider, not narrower.

**Highlights — fixes for adversarial-review findings F1–F3 + F6**

- **F1 (HIGH) — `TokenProtectorFactory` is now `public`.** Was `internal` in v1.9.2, which (because Common is ILRepack-internalized into every consumer) made `IsDegradedToPlaintext` and `LastDiagnostics` *unreachable* from plugin code. The "loud at startup" warning the design depends on couldn't be logged by any plugin. Now plugins can read both surfaces from their startup code: `if (TokenProtectorFactory.IsDegradedToPlaintext) logger.Warn("Token protection degraded: {Reason}", TokenProtectorFactory.LastDiagnostics?.DegradedReason);`.
- **F2 (HIGH) — `_degraded` flag now reflects the most-recent call, not OR-of-all-time.** Was sticky-set forever in v1.9.2 once any call failed; a subsequent successful call (transient I/O blip recovered, multi-plugin host) left the flag stuck at true and every downstream consumer would lie about its own state. Now `PublishDiagnostics` clears the flag to 0 on a successful (non-degraded) call.
- **F3 (HIGH) — Null-protector envelopes use distinct `lpc:plain:v1:` prefix.** v1.9.2 wrapped `NullTokenProtector` output in the same `lpc:ps:v1:` envelope as real ciphertext — visually indistinguishable on casual inspection. An operator querying their settings DB with `WHERE value LIKE 'lpc:ps:v1:%'` to find "encrypted secrets" would have hit null-mode plaintext too. Now `StringTokenProtector` chooses the envelope prefix based on the wrapped protector's `AlgorithmId`: real backends use `lpc:ps:v1:`, the null fallback uses `lpc:plain:v1:`. `IsProtected` returns true for both shapes so the setter's "already-protected" round-trip still works.
- **F6 (MED) — Catch filter narrowed.** v1.9.2 used `catch (Exception ex) when (!requireBackend)` which swallowed `CryptographicException` from a corrupted keychain / key ring (the operator wants to see that, not a misleading plaintext fallback), plus `OutOfMemoryException` and similar process-fatal signals. Now `IsExpectedBackendInitFailure` whitelists the I/O families (`IOException`, `UnauthorizedAccessException`, `PlatformNotSupportedException`, `DllNotFoundException`, `EntryPointNotFoundException`) and propagates everything else.

**Test coverage additions**

- `RealBackend_WrappedByStringTokenProtector_UsesProtectedPrefix` — pins F3.
- `NullProtector_WrappedByStringTokenProtector_UsesPlaintextPrefix_AndRoundTrips` — replaces the v1.9.2 test that asserted the now-incorrect `lpc:ps:v1:` shape.
- `CreateFromEnvironment_ClearsStickyDegradedFlag_OnSubsequentSuccess` — pins F2.
- `TokenProtectorFactory_TypeIsPublic_SoDownstreamPluginsCanReadDiagnostics` — pins F1 via reflection (so a future re-internalization breaks the build).
- `CreateFromEnvironment_DoesNotSwallow_CryptographicException` — pins F6.
- `LastDiagnostics_ExposesBackendNameAndDegradedReason_AfterDegradation` — confirms the diagnostic surface contract.

Deferred to a follow-up (acknowledged but not blocking v1.9.3):
- F4 (TOCTOU + leak in `EnsureKeysDirIsWritable` probe).
- F5 (`IsUsableRootedDir` only checks rooted, not writable — first rooted candidate wins even if unwritable).
- F7 (Windows `ApplicationData` vs `LocalApplicationData` for Roaming users).
- F8 (no event/callback surface beyond the static snapshot).

**Breaking changes:** None
**Deprecations:** None
**Dependency changes:** None

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.9.2...v1.9.3)

## [1.9.2] - 2026-05-23
**Upgrade note:** Hot-fix for a Lidarr Docker startup failure that affected every plugin consuming Common's token protection. The DataProtection key-dir resolver no longer falls back to a *relative* path when `$HOME` is empty; the factory degrades gracefully to a `NullTokenProtector` (with a loud one-line diagnostic) when the backend can't initialise. Plugins do not need code changes — bump the submodule pointer.

**Highlights**
- **`DataProtectionTokenProtector.GetDefaultKeysDir` candidate chain.** Previous implementation returned `Path.Combine(home, ".config", …)` where `home` came from `Environment.SpecialFolder.UserProfile`. In hotio / linuxserver Lidarr Docker images, the abc user's home directory is sometimes absent from `/etc/passwd` after PUID/PGID adjustments; `UserProfile` returns empty; the resulting `Path.Combine("", ".config", …)` is a *relative* path that resolves against the process cwd (`/app/bin`, the read-only install dir). `Directory.CreateDirectory` then fails with `UnauthorizedAccessException: Access to the path '/app/bin/.config' is denied`, which propagated up through `StringTokenProtector.Protect` → every `set_ApiKey` call. The new resolver chains: `$XDG_DATA_HOME` → `$XDG_CONFIG_HOME` → `SpecialFolder.LocalApplicationData` → `$HOME/.local/share` → `SpecialFolder.ApplicationData` → `$HOME/.config` → `Path.GetTempPath()`. Every candidate is checked with `Path.IsPathRooted`; non-rooted candidates are skipped (this is the rule that prevents the bug from recurring).
- **`NullTokenProtector` graceful fallback.** When the configured backend fails to initialise (typically: key dir unwritable), the factory now substitutes a `NullTokenProtector` that returns plaintext bytes verbatim. The wrapping `StringTokenProtector` still produces a well-formed envelope, but the embedded algorithm id is `"null"` (base64-url-encoded as `bnVsbA`) so an audit can recognise unprotected blobs at a glance. `TokenProtectorFactory.IsDegradedToPlaintext` flips to `true`; `LastDiagnostics` carries the cause and the path that failed, so plugin-startup code can surface the degradation to the operator.
- **`LP_COMMON_REQUIRE_PROTECTOR=true` opt-in for hard-fail mode.** Production deployments that would rather see the plugin fail loudly than silently store secrets as plaintext can set this env var; the factory then propagates the initialisation exception instead of substituting `NullTokenProtector`.
- **`LP_COMMON_PROTECTOR=null` explicit opt-in.** Dev environments where the operator accepts plaintext storage for convenience can request the null backend by name; honoured even when `LP_COMMON_REQUIRE_PROTECTOR=true`.
- **Pre-flight write probe.** Before constructing the DataProtection provider, the factory now creates+deletes a 0-byte probe file in the candidate keys dir. Surfaces "directory not writable" failures with a clearer error path than waiting for the first `Protect` call to throw.
- **`TokenProtectorDiagnostics` record.** Public surface so plugin startup code can read `BackendName`, `KeysPath`, `Cause`, and `DegradedReason`. Recommended log line on plugin startup: `"Token protection: {BackendName} (keys={KeysPath ?? "(in-memory)"})"`.

**Test coverage**
- `tests/Security/TokenProtection/TokenProtectorFactoryFallbackTests.cs` (NEW, 7 tests):
  - `GetDefaultKeysDir_AlwaysReturnsRootedPath_EvenWhenHomeAndXdgAreEmpty` — pins the original bug fix.
  - `GetDefaultKeysDir_FallsBackToTempPath_AsLastResort` — verifies the temp-path safety net.
  - `GetDefaultKeysDir_PrefersXdgDataHome_WhenSet` — XDG ordering.
  - `CreateFromEnvironment_FallsBackToNullProtector_WhenKeysDirIsUnwritable` — graceful degradation.
  - `CreateFromEnvironment_Throws_WhenRequireProtectorIsSet_AndBackendFails` — hard-fail opt-in.
  - `CreateFromEnvironment_ExplicitNullMode_ReturnsNullProtector_WithoutInitialisingBackend` — explicit null.
  - `NullProtector_WrappedByStringTokenProtector_RoundTripsAndIdentifiesAsNull` — audit-visibility.

**Breaking changes:** None
**Deprecations:** None
**Dependency changes:** None

**Affected plugins** (no code changes needed — bump the submodule pointer):
- Brainarr (BrainarrSettings.set_ApiKey)
- AppleMusicarr (AppleMusicSecretProtection / FileAppleMusicSettingsStore)
- Tidalarr (FileTokenStore<TidalTokens>)
- Qobuzarr (TokenRefresher, CredentialValidator)

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.9.1...v1.9.2)

## [1.9.1] - 2026-05-23
**Upgrade note:** Adds `Services.Diagnostics.HttpExceptionClassifier` on top of v1.9.0. Plugins that surface categorised connection-test failures in their `Test()` indicator (auth / rate-limit / network / server) can now consume this from Common instead of copy-pasting. Tidalarr's HTTP-error categorisation is the first consumer.

**Highlights**
- `Services.Diagnostics.HttpExceptionClassifier` — classifies `HttpRequestException` / `TaskCanceledException` chains into actionable categories (`UnauthorizedOrForbidden`, `RateLimited`, `NetworkUnreachable`, `Timeout`, `ServerError`, `Unknown`). Used by streaming-plugin `Test()` methods to surface a category-tagged failure message in the Lidarr settings UI instead of "connection failed".

**Breaking changes:** None
**Deprecations:** None
**Dependency changes:** None

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.9.0...v1.9.1)

## [1.9.0] - 2026-05-23
**Upgrade note:** Adds the AuthFailureGate surface (fail-fast latch + delegating handler + per-key registry) plus SecureMemory, Conservative rate-limit profile, PagedResponseValidator, testkit-lifted plugin contracts, and `Lidarr.Plugin.*.dll` naming enforcement. The new surface is additive — `IUniversalAdaptiveRateLimiter.RecordAuthFailure` is a default interface method so existing alternate implementations continue to compile without changes.

**Highlights**
- **AuthFailureGate** (`src/Services/Bridge/`): fail-fast latch built on `IAuthFailureHandler` that prevents plugins from hammering an upstream service when authentication is known bad — the failure mode that previously got Qobuzarr users IP-banned. `TryAcquireProbeSlot()` rate-limits one network call per probe-interval while latched so the plugin can detect re-credentialing without spamming the upstream. Per-key `AuthFailureGateRegistry` for multi-provider plugins (brainarr's 11 LLM providers each get an isolated gate). `AuthFailureDelegatingHandler` auto-wires the gate into any `AddHttpClient` typed client. New `HandleFailureAsync` / `HandleSuccessAsync` pass-throughs on the gate let callers signal without touching the underlying `IAuthFailureHandler` (which is internalized per plugin via ILRepack, so direct sharing across plugin ALC boundaries is unsafe).
- **SecureMemory** + **Conservative rate-limit profile** + **PagedResponseValidator** consumed by applemusicarr's APL-006 / APL-009 / APL-010 closures.
- **5 adversarial-review must-fixes**:
  1. `IUniversalAdaptiveRateLimiter.RecordAuthFailure` is a default interface method (no compile break in alternate impls).
  2. `SecureMemory.ZeroPemKey` short-circuits on length-0 strings (would otherwise corrupt the interned `string.Empty`).
  3. `LogRedactor` split into bare-word + URL-query-context patterns; bare `state=available`, `status code=ETIMEDOUT`, etc. no longer false-positive-redact.
  4. `PublicAPI.Unshipped.txt` baselines populated for the full new surface (clears RS0016 warning flood downstream).
  5. `AuthFailureGate` cross-ALC hazard documented; pass-through methods added so callers don't need to touch the `Handler` property.
- **TestKit plugin contracts lifted** (`testkit/Compliance/`): `PluginVersionContract`, `PluginPackagingContract`, `PublishedReleaseInstallability` are now part of the public TestKit so downstream plugins can run the same compliance checks without copy-paste.
- **`Lidarr.Plugin.*.dll` naming convention** enforced by `PluginPackagingContract`. Lidarr's `PluginLoader` silently ignores any other DLL filename — the contract catches it at build time instead of at runtime.

**Breaking changes:** None
**Deprecations:** None
**Dependency changes:** None

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.8.0...v1.9.0)

## [1.8.0] - 2026-05-23
**Upgrade note:** Ecosystem version contract, parity-lint forbiddenFields enforcement, ALC fix, and async rate-limit refactor. No breaking API changes.

**Highlights**
- Ecosystem version contract (`versionContract`) added to `scripts/parity-spec.json`; plugin CI should call `ecosystem-parity-lint.ps1 -Check VersionContract` to enforce that a plugin's `VERSION` file and manifest version align with the Common library version.
- `forbiddenFields` enforcement wired into parity-lint — fields listed as forbidden in `parity-spec.json` now cause lint failures.
- ALC multi-plugin co-existence fix: isolated `AssemblyLoadContext` per plugin prevents type-identity collisions when multiple streaming plugins are loaded simultaneously.
- `StreamingPluginMixins.StreamingIndexerMixin.ApplyRateLimitAsync`: replaced `Task.Delay(...).Wait()` inside a lock with `SemaphoreSlim` + `await Task.Delay`, eliminating the blocking-wait-inside-lock anti-pattern and enabling proper async flow.
- `SmartCache` test suite: removed `Skip` from two flaky tests (`TryGet_ReturnsFalseForExpiredItem`, `Eviction_LowPriorityItemsEvictedFirst`) by injecting a `TimeProvider` for deterministic time control.

**Breaking changes:** None
**Deprecations:** None
**Dependency changes:** None

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.7.1...v1.8.0)

## [1.7.1] - 2026-03-27
**Upgrade note:** Patch release hardening bridge defaults, sandbox resilience, and test coverage. No new public API surface.

**Highlights**
- `DefaultDownloadStatusReporter` added; `AddBridgeDefaults()` now registers 4 reporters (auth, indexer, rate-limit, download status)
- `PluginSandbox` hardening: `ReflectionTypeLoadException` handling, single `IPlugin` enforcement, `PluginType` option, `DefaultHostVersion` updated to 3.1.2.4913
- Thread-safe bridge singletons (`volatile` + `lock` pattern)
- Compliance test rewrite: fixture-backed `BridgeComplianceTests` with `BridgeComplianceFixture` caching
- 74 new tests: `MemoryHealthMonitor`, `StreamingApiRequestBuilder`, rate limit edge cases
- Guard patterns (`ArgumentNullException.ThrowIfNull`) throughout bridge layer
- `GetTypes` restored (from `GetExportedTypes`) for broader type discovery

**Breaking changes:** None
**Deprecations:** None
**Dependency changes:** None

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.7.0...v1.7.1)

## [1.7.0] - 2026-03-26
**Upgrade note:** Bridge contracts shipped. 8 new Abstractions interfaces for host-boundary communication (auth, indexer, rate-limit, download status). Default implementations and DI extensions in Common. First consumer: Tidalarr.

**Highlights**
- Bridge contract interfaces: `IAuthFailureHandler`, `IIndexerStatusReporter`, `IRateLimitReporter`, `IDownloadStatusReporter`, `IIndexerRequestBuilder`, `IIndexerResponseParser<T>`, `IIndexerWithMetadata`, `IRssFeedProvider`
- Default implementations: `DefaultAuthFailureHandler`, `DefaultIndexerStatusReporter`, `DefaultRateLimitReporter` with logging and state tracking
- DI extension: `AddBridgeDefaults()` registers all defaults via `TryAddSingleton`
- Fixture-backed compliance tests: `BridgeComplianceTests` (15 behavioral) + `BridgeDefaultsActivationTests` (4 DI activation)
- TestKit: `PluginSandbox` for isolated ALC plugin loading, `BridgeComplianceFixture` for bridge contract testing
- Behavioral docs: `BRIDGE_RUNTIME_CONTRACTS.md` with triggers, reliability, host assumptions

**Breaking changes:** None
**Deprecations:** None
**Dependency changes:**
- CliWrap 3.10.0 -> 3.10.1
- coverlet.collector 8.0.0 -> 8.0.1
- DataProtection 8.0.x -> 8.0.25
- azure/login v2 -> v3
- release-drafter v6 -> v7

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.6.0...v1.7.0)

## [1.6.0] - 2026-03-11
**Upgrade note:** Major infrastructure release — ecosystem parity testing, sync-over-async cleanup, diagnostics namespace, and comprehensive dependency updates. No breaking API changes.

**Highlights**
- Ecosystem parity test infrastructure (lint + TestKit base class)
- Sync-over-async pattern elimination with lint enforcement
- Diagnostics namespace for non-LLM providers
- Packaging gates improvements (contents manifest, canonical Abstractions)
- Local CI runner (`local-ci.ps1`) for offline verification
- 6-Month Autonomous Development Roadmap (Phases 12-23)

**Breaking changes:** None
**Deprecations:** None
**Dependency changes:**
- Microsoft.CodeAnalysis.CSharp 4.10.0→4.14.0
- Microsoft.Extensions.Configuration.Json 8.0.0→8.0.1
- Microsoft.Extensions.TimeProvider.Testing 8.5.0→9.10.0
- System.Security.Cryptography.ProtectedData 8.0.0→9.0.14
- coverlet.collector 6.0.4→8.0.0
- xunit 2.9.2→2.9.3
- Spectre.Console 0.50.0→0.54.0
- Microsoft.NET.Test.Sdk 17.11.1→18.3.0
- Microsoft.Extensions.Logging.Abstractions 8.0.1→8.0.3
- JsonSchema.Net 7.2.3→7.4.0
- actions/download-artifact v4→v8, actions/upload-artifact v4→v7

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.5.0...v1.6.0)

### Added
- **Ecosystem Parity Infrastructure** (#393)
  - `scripts/parity-spec.json` — single source of truth for parity requirements
  - `scripts/ecosystem-parity-lint.ps1` — PowerShell structural lint for repos
  - `testkit/Compliance/EcosystemParityTestBase.cs` — C# xUnit base class
- **Diagnostics Namespace** (#331) — abstractions for non-LLM providers
- **Sync-Over-Async Lint** (#332) — `lint-sync-over-async.ps1` with allowlist support
- **SHA Pin Enforcement** (#330) — workflow SHA pinning lint
- **Contents Manifest Gate** (#333) — plugin ZIP closure validation
- **Local CI Runner** (#363) — `local-ci.ps1` for offline verification
- **Warning Budget Visibility** (#366) — build warning tracking
- **Canonical Reason Codes** (#380) — triage contracts
- **Diagnostic Error Codes** (#341) — `DiagnosticErrorCodes` for providers
- **Cross-Platform Path Validation** — `PathValidation.IsReasonablePath`
- **Comprehensive Documentation** — roadmap phases 12-23, KPI definitions, phase gate evidence

### Fixed
- **Sync-Over-Async Patterns** (#408) — `StreamingTokenManager.ClearSession()` now truly sync
- **Windows CI Credential Issue** (#383) — change detection step fix
- **Local CI Docker Exit Codes** (#364, #365) — reliable Docker exit code capture
- **ManifestCheck StrictMode** (#326) — null-safe access for optional manifest targets
- **PluginPack Cached Path** (#320) — temp dir cleanup race condition
- **Reserved Device Name Normalization** (#297) — cross-platform filesystem safety

### Changed
- **LLM Provider System**: Complete LLM provider abstraction layer
  - `ILlmProvider` interface with standard chat completion contract
  - `LlmRequest`/`LlmResponse` data contracts with message structure
  - `LlmErrorCode` enum with standardized error codes
  - `LlmProviderException` base exception with concrete exception types
    - `LlmAuthenticationException` for credential/authorization failures
    - `LlmRateLimitException` for rate limiting scenarios
    - `LlmProviderException` for general provider errors
    - `LlmNetworkException` for network-related failures
  - `LlmErrorMapper` utility for mapping provider-specific errors
- **Structured Logging for LLM Providers**
  - `LlmLoggerExtensions` for provider-specific structured logging
  - `LlmEventIds` for standardized LLM event codes (2000-2039 range)
  - `LogRedactor` for sensitive data masking in logs
- **Claude Code Provider**: Full Claude Code CLI integration
  - `ClaudeCodeProvider` implementing `ILlmProvider` interface
  - `ClaudeCodeSettings` configuration record
  - `ClaudeCodeDetector` for Claude CLI installation detection
  - `ClaudeCodeResponseParser` for NDJSON streaming response parsing
  - `Claude CLI JSON response` DTOs for deserialization
  - Capability probe for provider feature detection
- **CLI Infrastructure**: Reusable CLI execution framework
  - `ICliRunner` interface for cross-platform CLI execution
  - `CliRunner` implementation with CliWrap integration
  - Support for Windows .cmd/.bat execution via cmd.exe /c
  - Stdin/stdout stress tests for subprocess reliability
- **Streaming Decoders**: SSE streaming support
  - `SseStreamDecoder` for Server-Sent Events parsing
  - `ClaudeCodeNdjsonDecoder` for Claude-specific NDJSON format
  - Streaming token aggregation and response assembly
- **CI Improvements**
  - Lint enforcement for adoption guardrails
  - Submodule pinning with ext-common-sha.txt
- **Documentation**
  - ADR-001: Streaming architecture decision
  - ADR-002: Subscription auth research
  - Streaming support matrix
  - Tech debt registry
  - Expanded LLM provider documentation
  - Pre-merge tightening for v2 milestone
- **E2E Improvements**
  - Strictness promotion checker with actionable drift issues
  - Harden drift issue management + secrets validation

### Changed
- **Documentation Cleanup**: Removed deprecated redirect stubs
  - Removed docs/concepts/PLUGIN_ISOLATION.md (redirect stub)
  - Removed docs/concepts/COMPATIBILITY.md (redirect stub)
  - Removed docs/how-to/TEST_WITH_TESTKIT.md (redirect stub)
  - Fixed broken link paths after redirect removal

### Fixed
- **Exception References**: Corrected LlmProviderException cref namespace
- **Unresolvable Exception Cref**: Removed unresolvable exception cref in ILlmProvider
- **CLI Execution**: Added safe CLI defaults and fixed error mapping
- **Test Runner**: Robust TRX skip count and build hardening
- **Documentation**: Avoid unresolved exception cref in abstractions

### Tests
- Added `ClaudeCodeProvider` unit tests
- Added `ClaudeCodeDetector` unit tests
- Added `ClaudeCodeResponseParser` unit tests
- Added `CliRunner` unit tests
- Added CliRunner stdin/stdout stress tests

## [1.2.2] - 2025-10-19
**Upgrade note:** Maintenance release aligning PR #39 merge; no breaking changes.

**Highlights**
- Merge “Prepare for public release” PR (#39) with conflict resolution on top of 1.2.1 baseline.
- Documentation consistency (README Latest v1.2.2).
- No functional changes beyond those in 1.2.1.

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.2.1...v1.2.2)
## [1.2.1] - 2025-10-11
**Upgrade note:** Security + packaging polish. Encrypted token storage by default; safer feed configuration; improved CI supply-chain checks.

**Highlights**
- Token store: encrypt at rest with pluggable providers (DPAPI on Windows; Keychain on macOS; Secret Service or Data Protection on Linux). Auto-migrates legacy plaintext to v2 envelope.
- Packaging: default to public TagLibSharp; CI-only override for TagLibSharp-Lidarr via UseLidarrTaglib.
- CI security: CodeQL, SBOM generation + upload, dependency review gate, Gitleaks PR/push + full history workflows.
- Docs: SECURITY.md, CODE_OF_CONDUCT.md, TOKEN_PROTECTION.md, TAGLIB_DEPENDENCY.md; README updated.

**Breaking changes:** None
**Deprecations:** None
**Dependency changes:** Added Azure.* packages (net8) for optional AKV DP key wrapping.

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.1.7...v1.2.1)
## [1.1.7] - 2025-10-11
**Upgrade note:** Policy-first HTTP path wired end-to-end; safer dedup/caching defaults; clearer redirect semantics.

### Highlights
- Builder → Options → Executor integration: stamp endpoint/profile/params/scope; executor consumes them for per-host|profile gates and stable keys.
- GET singleflight dedup when cache misses; race-guarded recheck; avoids caching failures/cancels.
- Query canonicalization for multivalue params (ordinal sort, lowercase percent-encoding).
- Redirects: 307/308 auto-follow preserving method/body; 301/302 auto-follow only for safe methods (GET/HEAD).
- Cache sliding TTL coalesced under concurrency; bounded by absolute expiration; short stale-grace for 304s.
- Conditional GET: ETag/Last-Modified persisted and revalidation path tested.
- Docs: OTel quickstart (feature flag `LPC_OTEL_ENABLE=1`) + sample Grafana dashboard.
- Template: `dotnet new lidarr-plugin` with minimal settings/module/indexer and a passing test; includes cache policy defaults and OAuth2 settings stub.
- Analyzers (dev dependency): LPC0001 avoid raw HttpClient usage; LPC0002 prefer policy-based overload.

**Breaking changes:** None
**Deprecations:** None (legacy overloads may be hidden in next minor)
**Dependency changes:** None

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.1.6...v1.1.7)

- Carved out `Lidarr.Plugin.Abstractions` as the host-owned ABI package with public API analyzers and AssemblyLoadContext guidance.
- `NuGet.config` maps TagLibSharp packages to the public Lidarr Azure Artifacts feed so CI restores `TagLibSharp-Lidarr` 2.2.0.27.
- `Directory.Build.props` pins `<AssemblyVersion>`/`<FileVersion>` to `10.0.0.35686` (Lidarr 2.14.2.4786 host) so every downstream plugin consumes matching binaries.
- `scripts/verify-assemblies.ps1` copies host assemblies, validates `FileVersion` <-> `AssemblyVersion`, and fails fast when the Lidarr output folder is missing.
- `scripts/prepare-host-stub.ps1` generates a `10.0.0.35686` Lidarr host stub so CI and clean machines can satisfy verification without the full Lidarr repo.
- `scripts/setup-lidarr.ps1` mirrors Brainarr's bootstrap to clone/update Lidarr into `../Lidarr` and optionally build or stub host assemblies for validation.
- Solution now includes `Lidarr.Plugin.Common.Tests` so CI collects coverage and enforces regression suites.
- `.github/workflows/pr-validation.yml` enforces the verification script, `dotnet build -c Release -warnaserror:NU1903`, and `dotnet test -c Release --no-build` on every pull request.
- `docs/UNIFIED_PLUGIN_PIPELINE.md` describes the shared platform repo, version-gated CI, ILRepack guardrails, release orchestration, packaging, and monitoring expectations for plugins.
- `TokenDelegatingHandler` and `ContentDecodingSnifferHandler` provide reusable bearer-token injection and mislabelled gzip recovery across all plugins.

### Changed
- Request deduplication now cancels in-flight tasks when the deduplicator is disposed and uses `TrySet*` guards to avoid race exceptions.
- README Maintainer Checklist now references the Lidarr setup script for host bootstrap.
- CI only runs the test-result annotation step on Linux runners to avoid the Windows container limitation.
- README gains a Maintainer Checklist and Plugin Version Governance section referencing the sync script and unified pipeline playbook.
- NuGet dependencies (System.Text.Json, Microsoft.Extensions.* , Newtonsoft.Json) updated to the latest 6.0.x/13.0.x patches to clear NU1903 advisories during Release builds.
- `HttpClientExtensions.GetJsonAsync<T>` now verifies Content-Type and includes payload previews when responses are not JSON.
- Removed unused Polly packages and disabled `AllowUnsafeBlocks` in the library project to avoid accidental unsafe usage.
- Multi-targeted the library for net6.0 and net8.0 with conditional Microsoft.Extensions dependency versions.
- BaseStreamingIndexer now accepts an optional HttpClient factory for DI scenarios.

### Deprecated
- Marked `IAdaptiveRateLimiter` / `AdaptiveRateLimiter` as obsolete; migrate to `IUniversalAdaptiveRateLimiter` and `UniversalAdaptiveRateLimiter`.

### Reminder
- Maintainers must keep host assemblies in sync with Lidarr 2.14.2.4786 before shipping plugin updates; see `docs/UNIFIED_PLUGIN_PIPELINE.md` for the complete process.

## [1.1.6] - 2025-10-11
**Upgrade note:** No public API changes. Improves diagnostics and hardens concurrency + caching behavior under load.

### Highlights
- New `PluginOperationResultJson` helper for consistent, structured diagnostics across plugins.
- 304 revalidation path: add a brief stale grace to avoid races and preserve cached bodies when validators hit right at TTL.
- Windows file concurrency: `FileTokenStore` now writes via unique temp files and retries atomic replace/move to avoid transient sharing violations.
- Retry semantics: when honoring `Retry-After` absolute dates, do not add jitter; still clamped by the retry budget.
- CI: grant permissions for PR test result annotations; coverage summary appears reliably on PRs.

**Breaking changes:** None
**Deprecations:** None
**Dependency changes:** None

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.1.5...v1.1.6)

## [1.1.5] - 2025-10-01
**Upgrade note:** No public API changes. CLI `config` commands now persist settings consistently and surface the latest metadata across hosts.

### Highlights
- CLI `config` commands persist settings through `PluginHost`, apply invariant culture conversions, and mask sensitive values the same way on .NET 6.0 and .NET 8.0.
- `config show` renders output from the settings metadata so new properties appear automatically.
- `config reset` clears persisted storage, rewrites defaults from the host cache, and avoids stale state on reruns.
- Added `ConfigCommandTests` to cover typed updates, invalid values, secret masking, and reset behaviour.

**Breaking changes:** None
**Deprecations:** None
**Dependency changes:** None

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.1.4...v1.1.5)
## [1.1.4] - 2025-09-30
**Upgrade note:** No public API changes. New TestKit and resilience upgrades make plugin integration tests and HTTP handling more reliable.

### Highlights
- Introduced `Lidarr.Plugin.Common.TestKit` with reusable plugin sandbox, HTTP simulators, settings bridge, and log-capturing host context.
- Added `docs/how-to/TEST_WITH_TESTKIT.md` plus sample `PluginSandboxTests` harness.
- Centralised host semaphore management in `HostGateRegistry` and shared it with the generic resilience executor.
- HTTP helpers accept per-request timeouts, raise `TimeoutException`, and reuse the host gate registry; response cache trims oldest entries when over size limits.
- `ContentDecodingSnifferHandler` peeks only the gzip header, clears encoding metadata after inflation, and streams large bodies without buffering.
- Packaging excludes `docs/**` and `examples/**` from the nupkg, with updated release notes and metadata.

**Breaking changes:** None
**Deprecations:** `AdaptiveRateLimiter` (removal planned for 1.3)
**Dependency changes:** Updated Microsoft.Extensions.* and System.Text.Json patch versions across net6.0/net8.0 builds

[Full diff](https://github.com/RicherTunes/Lidarr.Plugin.Common/compare/v1.1.3...v1.1.4)
## [1.1.3] - 2025-09-03

### Added
- Introduced `Lidarr.Plugin.Common.TestKit` with the reusable plugin sandbox, HTTP simulators, settings bridge, and log-capturing host context shared by the core library.
- Added `docs/how-to/TEST_WITH_TESTKIT.md` and a sample `PluginSandboxTests` harness demonstrating isolated plugin loading and HTTP edge-case helpers.
- `BaseStreamingIndexer`: streaming search helpers (`SearchAlbumsStreamAsync`, `SearchTracksStreamAsync`, `FetchPagedAsync<T>`) plus deduplication on title/artist/year.
- `BaseStreamingDownloadClient`: overridable retry hook with `Retry-After` handling and jittered backoff; configurable retry counts per service.

### Notes
- All additions are non-breaking; existing list-based APIs remain fully supported.

## [1.1.2] - 2025-09-03

### Added
- Introduced `Lidarr.Plugin.Common.TestKit` with the reusable plugin sandbox, HTTP simulators, settings bridge, and log-capturing host context shared by the core library.
- Added `docs/how-to/TEST_WITH_TESTKIT.md` and a sample `PluginSandboxTests` harness demonstrating isolated plugin loading and HTTP edge-case helpers.
- Preview detection improvements: duration threshold (default ~90s), extended URL markers, additional heuristics.
- Validation enhancements: `ValidateFileSignature` for FLAC/OGG/MP4/M4A/WAV and richer overloads of `ValidateDownloadedFile`.
- Hashing/signing utilities: `ComputeSHA256`, `ComputeHmacSha256`, and `IRequestSigner` implementations (`Md5ConcatSigner`, `HmacSha256Signer`).
- File system hardening: NFC normalization, expanded reserved-name guard.
- Settings: `Locale` property on `BaseStreamingSettings` (default `en-US`).

### Changed
- Documentation refreshed to reference the new utilities and configuration options.

### Notes
- Changes are additive and backward compatible; submodule consumers can adopt incrementally.

## [1.1.1] - 2025-09-03

### Added
- Introduced `Lidarr.Plugin.Common.TestKit` with the reusable plugin sandbox, HTTP simulators, settings bridge, and log-capturing host context shared by the core library.
- Added `docs/how-to/TEST_WITH_TESTKIT.md` and a sample `PluginSandboxTests` harness demonstrating isolated plugin loading and HTTP edge-case helpers.
- Context-specific sanitizers: `Sanitize.UrlComponent`, `Sanitize.PathSegment`, `Sanitize.DisplayText`, `Sanitize.IsSafePath`.
- HTTP resilience: `HttpClientExtensions.ExecuteWithResilienceAsync` with 429/Retry-After awareness, jittered backoff, retry budgets, and per-host concurrency gating.
- OAuth token refresh: `OAuthDelegatingHandler` for bearer injection and single-flight refresh on 401.
- Atomic/resumable downloads: `.partial` staging, atomic moves, resume on 206.
- Model metadata: `StreamingAlbum.ExternalIds`, `StreamingTrack.ExternalIds`, `MusicBrainzId` support.

### Changed
- `BaseStreamingIndexer` now shares a resilient `HttpClient` pipeline to reduce socket exhaustion.
- Legacy `InputSanitizer` methods marked `[Obsolete]` in favor of the context-specific helpers.

### Notes
- No breaking changes; obsolete APIs remain available for compatibility.

## [1.1.0] - 2025-08-30

### Added
- Introduced `Lidarr.Plugin.Common.TestKit` with the reusable plugin sandbox, HTTP simulators, settings bridge, and log-capturing host context shared by the core library.
- Added `docs/how-to/TEST_WITH_TESTKIT.md` and a sample `PluginSandboxTests` harness demonstrating isolated plugin loading and HTTP edge-case helpers.
- OAuth/PKCE authentication base classes and token lifecycle helpers.
- Core streaming indexer/download client frameworks.
- Performance and memory management helpers (batch manager, monitors).

### Notes
- Prepared the library for packaging with Source Link and symbols.

## [1.0.0] - 2025-08-26

### Added
- Introduced `Lidarr.Plugin.Common.TestKit` with the reusable plugin sandbox, HTTP simulators, settings bridge, and log-capturing host context shared by the core library.
- Added `docs/how-to/TEST_WITH_TESTKIT.md` and a sample `PluginSandboxTests` harness demonstrating isolated plugin loading and HTTP edge-case helpers.
- Base classes: `BaseStreamingSettings`, `BaseStreamingIndexer<T>`, `BaseStreamingDownloadClient<T>`, `BaseStreamingAuthenticationService<T>`.
- Services: `StreamingResponseCache`, `StreamingApiRequestBuilder`, `QualityMapper`, `PerformanceMonitor`, `StreamingPluginModule`.
- Models: `StreamingArtist`, `StreamingAlbum`, `StreamingTrack`, `StreamingQuality`, `StreamingQualityTier`.
- Utilities: `FileNameSanitizer`, `HttpClientExtensions`, `RetryUtilities`.
- Testing support: `MockFactories`, `TestDataSets`.
- Interfaces: `IStreamingAuthenticationService<T>`, `IStreamingResponseCache`, `IQueryOptimizer`.

### Highlights
- 60–75% code reduction for new streaming plugins, thread-safe operations, built-in security, performance optimizations, comprehensive error handling, and rich documentation/examples.

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


