# Technical Debt Registry

This document tracks technical debt items across the Lidarr Plugin Ecosystem.

## Priority Levels

- **P0**: Critical - Blocking development or causing production issues
- **P1**: High - Should be addressed in next sprint
- **P2**: Medium - Address when working in related area
- **P3**: Low - Nice to have, address opportunistically

## Brainarr

### Large Files (Refactoring Candidates)

Line counts updated 2026-03-10 from `main` branch.

| File | Lines | Priority | Notes |
|------|-------|----------|-------|
| `Brainarr.Plugin/Services/Caching/EnhancedRecommendationCache.cs` | 805 | P2 | Cache logic could use strategy pattern |
| `Brainarr.Plugin/Services/Core/BrainarrOrchestrator.cs` | 639 | P2 | Main orchestration — smaller than originally listed, still the largest orchestrator |
| `Brainarr.Plugin/Services/Core/LibraryAnalyzer.cs` | 569 | P3 | Analysis logic — significantly reduced from earlier versions |
| `Brainarr.Plugin/Services/LibraryAwarePromptBuilder.cs` | 359 | P3 | Now reasonably sized |
| `Brainarr.Plugin/BrainarrSettings.cs` | 183 | — | No longer a debt item (settings container, cohesive) |

### Overlapping Provider Bases

| Base Class | Path | Lines | Purpose |
|------------|------|-------|---------|
| `SecureProviderBase` | `Brainarr.Plugin/Services/Providers/SecureProviderBase.cs` | 387 | Providers with credential handling |
| `BaseCloudProvider` | `Brainarr.Plugin/Services/Providers/BaseCloudProvider.cs` | 311 | Generic cloud API abstraction |

`HttpChatProviderBase` (previously listed) no longer exists — likely consolidated into one of the above.

**Recommendation:** Two bases is manageable. Revisit if a third appears or if confusion persists. P3.

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
| 2026-03-10 | FluentAssertions license risk | Decided: pin at 6.12.2 (MIT), dependabot ignore >= 7.0.0 |
| 2026-03-10 | Package Version Management (CPM) | Implemented Directory.Packages.props in all 3 plugin repos |
| 2026-03-10 | Ecosystem structural parity | Full parity achieved — PRs #393, #230, #218, #85 |
| 2026-01-30 | TRX skip count unreliable | Fixed in test-runner.psm1 (max fallback) |
| 2026-01-30 | Windows file-lock flakes | Fixed with build server hardening |
| 2026-01-30 | Streaming architecture unclear | ADR-001 documented decision |
| 2026-01-30 | Subscription auth unclear | ADR-002 documented decision |
