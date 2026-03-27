# Technical Debt Registry

This document tracks technical debt items across the Lidarr Plugin Ecosystem.

## Priority Levels

- **P0**: Critical - Blocking development or causing production issues
- **P1**: High - Should be addressed in next sprint
- **P2**: Medium - Address when working in related area
- **P3**: Low - Nice to have, address opportunistically

## Common — Deferred Items

| Item | Priority | Owner | Expiry | Rationale |
|------|----------|-------|--------|-----------|
| CLI Bridge Adapters | P2 | @plugin-maintainer | 2026-06-19 | Deferred. Native plugin patterns (ILRepack) work. Incomplete adapter stubs have been removed from the workspace. Revisit when CLI-first plugin workflow is prioritized. |
| Core Compliance Test Rewrite | P1 | TBD | 2026-04-30 | Current `CoreCapabilityComplianceTests` use mock scaffolding that validates mock behavior, not real plugin contracts. Needs fixture-backed tests against concrete bridge implementations. |
| Bridge Runtime Parity | P2 | TBD | TBD | v1.7.0 shipped bridge contracts (IAuthFailureHandler, IIndexerStatusReporter, IRateLimitReporter, etc.) in Abstractions `PublicAPI.Shipped.txt`. Default implementations exist in Common (`DefaultAuthFailureHandler`, `DefaultIndexerStatusReporter`, `DefaultRateLimitReporter`) registered via `AddBridgeDefaults()`. Remaining work: plugin-side integration — no plugin (Tidalarr, Qobuzarr) wires the contracts end-to-end yet. |

## Brainarr

### Large Files (Refactoring Candidates)

Line counts updated 2026-03-10 from `main` branch.

| File | Lines | Priority | Notes |
|------|-------|----------|-------|
| `Brainarr.Plugin/Services/Caching/EnhancedRecommendationCache.cs` | ~330 | ✅ Done | Extracted 8 types into separate files (PR pending) |
| `Brainarr.Plugin/Services/Core/BrainarrOrchestrator.cs` | ~520 | ✅ Done | Deduplicated FetchRecommendationsAsync overloads (PR pending) |
| `Brainarr.Plugin/Services/Core/LibraryAnalyzer.cs` | 569 | P3 | Analysis logic — significantly reduced from earlier versions |
| `Brainarr.Plugin/Services/LibraryAwarePromptBuilder.cs` | 359 | P3 | Now reasonably sized |
| `Brainarr.Plugin/BrainarrSettings.cs` | 183 | — | No longer a debt item (settings container, cohesive) |

### Provider Architecture — Dead Base Classes

| Base Class | Path | Lines | Used By |
|------------|------|-------|---------|
| `SecureProviderBase` | `Brainarr.Plugin/Services/Providers/SecureProviderBase.cs` | 387 | Test doubles only |
| `BaseCloudProvider` | `Brainarr.Plugin/Services/Providers/BaseCloudProvider.cs` | 311 | Nothing (OpenAICompatibleProvider extends it but is also unused) |

**Finding (2026-03-10):** All 11 concrete providers implement `IAIProvider` directly, bypassing both bases entirely. The real duplication is across the 11 provider implementations (~250+ lines each with similar HTTP/parsing/error-handling patterns). Consolidation opportunity is a provider factory or strategy pattern, not merging the two bases.

**Recommendation:** P3. Consider removing unused bases or refactoring providers to use them. Low urgency — the providers work correctly as-is.

## Common Library

### Streaming Decoders Without Consumer

| Decoder | Status | Notes |
|---------|--------|-------|
| `GeminiStreamDecoder` | Ready | No HTTP consumer (per ADR-001) |
| `ZaiStreamDecoder` | Ready | No HTTP consumer (per ADR-001) |

**Recommendation:** Keep for now (tested infrastructure). Revisit by 2026-07 if still unused.

## Cross-Plugin

### FluentAssertions License — DECIDED

**Decision (2026-03-10):** Pin at FA 6.12.2 (last MIT release). Do not upgrade to 7.x+ (commercial license).

- **Scope:** Only Qobuzarr uses FA (111 test files, ~2,900 `.Should()` call sites). Tidalarr, AppleMusicarr, and Common use plain xUnit `Assert.*`.
- **Migration cost:** ~2,900 call sites across 111 files — not justified for a test-only dependency that works fine on 6.12.2.
- **Protection:** Dependabot ignore rule added for `FluentAssertions >= 7.0.0` in Qobuzarr's `.github/dependabot.yml`.
- **Version pin comment:** Added to `Qobuzarr/Directory.Packages.props` explaining the MIT boundary.
- **New plugins:** Should use xUnit `Assert.*` (no FA dependency). This is already the pattern in Tidalarr, AppleMusicarr, and Common.

## Completed Items

| Date | Item | Resolution |
|------|------|------------|
| 2026-03-26 | Bridge contracts shipped + defaults | Contracts moved to PublicAPI.Shipped.txt; DefaultAuthFailureHandler, DefaultIndexerStatusReporter, DefaultRateLimitReporter implemented with AddBridgeDefaults() DI extension |
| 2026-03-10 | FluentAssertions license risk | Decided: pin at 6.12.2 (MIT), dependabot ignore >= 7.0.0 |
| 2026-03-10 | Package Version Management (CPM) | Implemented Directory.Packages.props in all 3 plugin repos |
| 2026-03-10 | Ecosystem structural parity | Full parity achieved — PRs #393, #230, #218, #85 |
| 2026-01-30 | TRX skip count unreliable | Fixed in test-runner.psm1 (max fallback) |
| 2026-01-30 | Windows file-lock flakes | Fixed with build server hardening |
| 2026-01-30 | Streaming architecture unclear | ADR-001 documented decision |
| 2026-01-30 | Subscription auth unclear | ADR-002 documented decision |
