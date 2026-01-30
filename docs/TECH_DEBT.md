# Technical Debt Registry

This document tracks technical debt items across the Lidarr Plugin Ecosystem.

## Priority Levels

- **P0**: Critical - Blocking development or causing production issues
- **P1**: High - Should be addressed in next sprint
- **P2**: Medium - Address when working in related area
- **P3**: Low - Nice to have, address opportunistically

## Brainarr

### Large Files (Refactoring Candidates)

| File | Lines | Priority | Notes |
|------|-------|----------|-------|
| `LibraryAwarePromptBuilder.cs` | 1924 | P2 | Prompt building could be split by concern |
| `LibraryAnalyzer.cs` | 1412 | P2 | Analysis logic could be extracted |
| `BrainarrSettings.cs` | 1230 | P3 | Settings container - large but cohesive |
| `BrainarrOrchestrator.cs` | 903 | P1 | Main orchestration - consider extracting strategies |
| `EnhancedRecommendationCache.cs` | 808 | P2 | Cache logic could use strategy pattern |

### Overlapping Provider Bases

- `BaseCloudProvider`
- `HttpChatProviderBase`
- `SecureProviderBase`

**Recommendation:** Pick one abstraction and migrate. Currently creates confusion about which to extend.

## Common Library

### Streaming Decoders Without Consumer

| Decoder | Status | Notes |
|---------|--------|-------|
| `GeminiStreamDecoder` | Ready | No HTTP consumer (per ADR-001) |
| `ZaiStreamDecoder` | Ready | No HTTP consumer (per ADR-001) |

**Recommendation:** Keep for now (tested infrastructure). Revisit in 6 months if still unused.

## Cross-Plugin

### FluentAssertions License Warning

FluentAssertions emits commercial license warning in test runs. Options:
1. Purchase license for commercial use
2. Migrate to NUnit assertions or Shouldly
3. Suppress warning (document decision)

### Package Version Management

Each plugin manages its own package versions. Consider:
- Central Package Management (Directory.Packages.props)
- Would reduce version drift across repos

## Completed Items

| Date | Item | Resolution |
|------|------|------------|
| 2026-01-30 | TRX skip count unreliable | Fixed in test-runner.psm1 (max fallback) |
| 2026-01-30 | Windows file-lock flakes | Fixed with build server hardening |
| 2026-01-30 | Streaming architecture unclear | ADR-001 documented decision |
| 2026-01-30 | Subscription auth unclear | ADR-002 documented decision |
