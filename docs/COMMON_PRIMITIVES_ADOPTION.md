# Common Primitives — Adoption Matrix

A reviewer-aid navigation index for the 2026-05-10 / 2026-05-11 UX-and-unification arc. Each row is one primitive added to `Lidarr.Plugin.Common`; the columns track which plugins consume it.

## Primitives shipped

| Primitive | PR | Lines | Tests | Where it lives |
|---|---|---|---|---|
| `RateLimitHeaderUtilities` | [#492](https://github.com/RicherTunes/Lidarr.Plugin.Common/pull/492) | +184 | 12 | `Services/Http/` |
| `UniversalAdaptiveRateLimiterOptions` | [#496](https://github.com/RicherTunes/Lidarr.Plugin.Common/pull/496) | +229 | 10 | `Services/Performance/` |
| `LrclibClient` | [#497](https://github.com/RicherTunes/Lidarr.Plugin.Common/pull/497) | +422 | 14 | `Services/Lyrics/` |
| `DownloadPathValidator` | [#498](https://github.com/RicherTunes/Lidarr.Plugin.Common/pull/498) | +291 | 10 | `Services/Validation/` |
| `HttpExceptionClassifier` | [#499](https://github.com/RicherTunes/Lidarr.Plugin.Common/pull/499) | +337 | 15 | `Services/Diagnostics/` |

**5 primitives + 61 TDD test cases.**

## Consumer adoptions

| Primitive ↓  / Plugin → | qobuzarr | applemusicarr | tidalarr | brainarr |
|---|---|---|---|---|
| `RateLimitHeaderUtilities` | [#266](https://github.com/RicherTunes/Qobuzarr/pull/266) | n/a (no rate-limit handler) | blocked on #276 | n/a (different domain) |
| `UniversalAdaptiveRateLimiterOptions` | n/a (no RPS setting) | [#126](https://github.com/RicherTunes/AppleMusicarr/pull/126) | n/a (no RPS setting) | n/a |
| `LrclibClient` | n/a (no lyrics flow) | not yet wired | not yet wired | n/a |
| `DownloadPathValidator` | [#265](https://github.com/RicherTunes/Qobuzarr/pull/265) | [#127](https://github.com/RicherTunes/AppleMusicarr/pull/127) | [#281](https://github.com/RicherTunes/Tidalarr/pull/281) | n/a (import-list only) |
| `HttpExceptionClassifier` | [#267](https://github.com/RicherTunes/Qobuzarr/pull/267) | n/a (bridge plugin, no Test() surface) | [#282](https://github.com/RicherTunes/Tidalarr/pull/282) | n/a |

**8 consumer PRs across 3 plugin repos.** Every primitive that has a viable consumer plugin has at least one demonstrated adoption.

## Adoption shapes

Each primitive's consumer PRs follow one of three predictable patterns. Reviewers can read one of each shape and skim the rest.

### Shape A — Inline-swap
> Plugin has its own inline logic that duplicates the new primitive verbatim. PR swaps the inline copy for the common helper.

- qobuzarr #266 (`RateLimitHeaderUtilities`): swaps two private static helpers (`BuildEndpointKey`, `ResolveRetryAfter`) that were byte-for-byte duplicates of common's helpers.
- qobuzarr #265 (`DownloadPathValidator`): swaps the inline `IsValidPath` helper that only ran `Path.GetFullPath` inside a try/catch.

### Shape B — Additive validation
> Plugin had no equivalent validation; PR adds it via the new primitive. Empty/missing input stays valid per existing UX contract; non-empty input gets validated.

- applemusicarr #127 (`DownloadPathValidator` on `OutputFolder`): empty stays valid ("use whatever Lidarr passes per-download — recommended"); non-empty paths are validated.
- applemusicarr #126 (`UniversalAdaptiveRateLimiterOptions`): user's `RequestsPerSecond` was previously stored-but-unused; now flows through the new ctor.

### Shape C — Extract-then-test
> Plugin's existing inline logic isn't testable in isolation (lives inside a method with too many dependencies). PR extracts a `public static` helper, then routes the helper through the new primitive.

- tidalarr #281 (`DownloadPathValidator`): extracted `ValidateDownloadPath` as a public static helper on `TidalLidarrDownloadClient`, parallel to the existing `ExtractAlbumIdFromGuid` static.
- tidalarr #282 (`HttpExceptionClassifier`): extracted `BuildTestFailureMessage` as a public static helper, swap the outer Test() catch.
- qobuzarr #267 (`HttpExceptionClassifier`): same extract-then-test pattern as tidalarr #282.

## Test coverage summary

| Primitive | Common tests | Consumer tests | Total |
|---|---|---|---|
| `RateLimitHeaderUtilities` | 12 | 3 (qobuzarr) | 15 |
| `UniversalAdaptiveRateLimiterOptions` | 10 | 8 (apple) | 18 |
| `LrclibClient` | 14 | 0 | 14 |
| `DownloadPathValidator` | 10 | 6 (qobuz) + 6 (apple) + 7 (tidal) | 29 |
| `HttpExceptionClassifier` | 15 | 7 (qobuz) + 7 (tidal) | 29 |
| **Total** | **61** | **44** | **105** |

## Stacking + merge order

These PRs are **stacked** — consumer PRs bump the common submodule to the parent common PR's branch HEAD. After common PRs merge to main, each consumer PR needs a rebase (or auto-merge when GitHub recognises the submodule SHA as resolved).

Suggested merge order to minimise rebase noise:

1. Common primitives in any order (no inter-dependencies):
   - #490 (audit punch list doc) + #491 (cargo-cult catches) — already mergeable
   - #492 (`RateLimitHeaderUtilities`)
   - #493 (mixin anti-pattern delete) + #494 (dead-mixin delete) — touch the same file; merge #493 first then rebase #494
   - #495 (cache observability) + #506 (test flake fix)
   - #496 (`UniversalAdaptiveRateLimiterOptions`)
   - #497 (`LrclibClient`)
   - #498 (`DownloadPathValidator`)
   - #499 (`HttpExceptionClassifier`)
2. Consumer PRs in any order after their parent common PR lands:
   - qobuzarr #265 / apple #127 / tidalarr #281 → after #498
   - apple #126 → after #496
   - qobuzarr #266 → after #492
   - qobuzarr #267 / tidalarr #282 → after #499

Plugin-side defect/feature PRs that **don't** depend on common changes can land in parallel:
- tidalarr #276-#280
- qobuzarr #261-#264
- brainarr #561
- applemusicarr #123-#125

## Not yet adopted

Three consumer slots remain open:

- **tidalarr `RateLimitHeaderUtilities`**: `TidalRateLimitingHandler` was modified in PR #276 (interface-correct signature). The same `BuildEndpointKey` / `ResolveRetryAfter` swap as qobuzarr #266 will land after #276 merges; it's a 1-file mechanical change with characterisation tests on the existing pattern.
- **tidalarr `LrclibClient`**: the audit identifies `UseLRCLIB` as a UI-disclosed-as-informational setting. Wiring requires a `TidalLyricsService` consumer that writes `.lrc` alongside audio at download-complete. Substantial — separate cycle, requires plumbing track metadata (artist/title/album/duration) through the download path.
- **applemusicarr `LrclibClient`**: same shape as tidalarr; Apple Music has its own lyrics API, so adoption would be a fallback path when Apple returns no synced lyrics.

Both `LrclibClient` consumer adoptions are bigger lifts than the other adoptions in this arc and were deliberately deferred.
