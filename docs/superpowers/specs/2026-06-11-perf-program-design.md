# Ecosystem Performance Program — Design (rev 3)

**Date:** 2026-06-11 (rev 2 same day after a 3-lens adversarial review refuted
rev 1's Phase-2 premise — see "Review record"; rev 3 same day: user expanded
scope to 20 phases covering Lidarr.Plugin.Common extensively and brainarr —
see "Roadmap" at the end, which renumbers the phases below)
**Scope:** qobuzarr, tidalarr, Lidarr.Plugin.Common, brainarr
**Driver:** Proactive performance sweep, with rate-limit behavior under parallel load
(heavy downloads + concurrent indexer searches) as the first-class concern.

## Problem statement (corrected)

The shared `UniversalAdaptiveRateLimiter` paces **per-endpoint** (key =
`host:firstPathSegment`), each endpoint with its own adaptive budget and slot
chain; the per-service semaphore is held only for an O(1) slot computation.
Consequently the rev-1 claim "downloads monopolize a single shared budget and
starve search" is wrong as stated: tidal CDN chunk fetches and search live in
**different buckets** and cannot contend inside the limiter.

The real, verified problems in this area are:

1. **qobuzarr search is not rate-limit-gated at all.** `QobuzIndexer` executes
   searches through Lidarr's host HTTP stack (`IHttpClient.ExecuteAsync`,
   `QobuzIndexer.cs:236`), which never traverses `QobuzRateLimitingHandler` or
   the limiter. The in-code comment claiming central rate limiting on that path
   is false. Search 429s are also invisible to the limiter's adaptation.
2. **Within-bucket contention is plausible only where search and download
   metadata share an API host bucket** (e.g. `api.tidal.com:v1` serves search
   AND the orchestrator's getAlbum/getTrack/playbackinfo calls). Whether this
   produces user-visible search starvation is **unproven** and must be
   demonstrated before any limiter redesign.
3. **qobuzarr uses ≥3 inconsistent endpoint-keying schemes** for direct
   `WaitIfNeededAsync` calls (full URL incl. query in `QobuzHttpClient.cs:71`;
   literal `"/album/search"` in `LidarrAlbumRetriever.cs:172`; `host:firstSeg`
   in the handler), splitting one logical endpoint across buckets that cannot
   see each other and creating unbounded bucket cardinality.
4. Baseline capture depends on the Common #84 observability hooks: adaptation
   and periodic limiter stats are now logged by `AdaptiveRateLimitingHandler`,
   but direct plugin limiter call sites still need consolidation before the
   whole upstream surface is measured uniformly.

Separately, an exploration sweep surfaced perf-hotspot candidates; adversarial
re-verification already culled them (one refuted, one hazardous-as-proposed,
one remedy-misdirected, one confirmed — see Phase 3). Historical false-positive
rate ~50–60%: **nothing is fixed without verification and a baseline implicating it.**

## Goals / success criteria

1. Search (interactive + RSS) remains responsive while the download queue is
   saturated — demonstrated on a live instance with before/after numbers, with
   the contention mechanism identified and named first.
2. All upstream traffic from both plugins is actually gated by the limiter
   (closing the qobuz search bypass), with consistent endpoint keying.
3. No regression in API safety: total request rate never exceeds today's
   adaptive budget; 429/Retry-After/auth-gate behavior unchanged.
4. Each shipped fix carries a measured delta (live harness for second-scale
   effects; micro-benchmarks for ms-scale effects).
5. TDD, one concern per PR, landed on Gitea, adversarial review per wave.

## Non-goals

- Newtonsoft→System.Text.Json migration in qobuzarr; sliding-expiration lock
  redesign in `StreamingResponseCache` — deferred unless baselines implicate them.
- amazonmusicarr / applemusicarr / brainarr feature work. (But: interface
  changes to `IUniversalAdaptiveRateLimiter` MUST be source-compatible for
  their test fakes — see Phase 2 — since they implement the interface.)

## Phase 0 — Ground truth + unblocking (NEW; gates everything)

1. **Canary push:** resolved 2026-07-02. Pushes were blocked server-side as of
   2026-06-10 (unpacker error / full disk), but the Gitea push path is verified
   working again; keep normal Gitea PRs and CI as the landing path.
2. **Prior-art triage:** two unmerged Common branches sit on the exact files
   this program touches — `perf/ratelimiter-lane` (9d1e8f3, same fairness
   topic) and `refactor/adaptive-ratelimit-dedup` (5d82071, edits
   `AdaptiveRateLimitingHandler`). Land, supersede, or close each explicitly
   before any Phase-2 PR.
3. **In-flight plugin branches:** land-or-park qobuzarr
   `fix/response-cache-endpoint-matching` (changes search-path cache
   effectiveness → corrupts baselines), tidalarr `feat/adopt-query-optimizer`
   and `fix/tidal-snapshot-field-drop`. Record baseline SHAs in every report.
4. **Contention localization:** demonstrate (or refute) search-vs-download
   contention deterministically: identify which buckets tidal search/metadata
   actually share, whether qobuz search delay even involves the limiter
   (per §1 it doesn't), and whether the contended resource is the limiter at
   all vs. connection pool / `GenericResilienceExecutor` gates / ThreadPool /
   Lidarr task queues. Fake-clock unit-level repro preferred over live load.
5. **Instrumentation pre-step (Common PR):** periodic stats logging (or a
   diagnostics dump hook) over the existing `GetServiceStats` surface, plus
   adaptation-event log lines (currently computed and discarded). Ships before
   baselines so before/after run on identical instrumented code.

**Gate:** Phase 2's limiter redesign proceeds only if Phase 0/1 demonstrates
user-visible search degradation attributable to a shared bucket. The qobuz
search-gating fix (Phase 2a) and keying consolidation (Phase 2b) proceed
regardless — they are correctness fixes, not perf speculation.

## Phase 1 — Load harness + baseline (descoped to what live runs can prove)

Harness at `C:\r\Alex\github\.perf-harness\`, reusing the existing e2e
substrate (Common `LidarrContainerFixture` TestKit + `e2e-local-runner.ps1`
per-plugin shims; dedicated containers, ports 8690/8692 — NOT the shared
`:8787` instance, which other programs use concurrently).

**Live scenarios are for second-scale effects only** (search-under-load,
starvation observation). Ms-scale claims are measured by micro-benchmarks.

- **Scale cap:** 2–3 fixed albums at lowest quality per run (real accounts;
  ToS/volume exposure capped). One 10-album one-shot validation at the end,
  not per-iteration.
- **Repeatability:** scripted reset between runs (delete trackfiles, clear
  queue + blocklist, container restart to reset in-memory limiter state);
  fixed pre-validated corpus that imports cleanly; first warm-up sample
  discarded; ≥5 repetitions per condition; report median + IQR; accept a delta
  only if it exceeds run-to-run spread.
- **Isolation:** the non-measured plugin's indexer + download client disabled
  per scenario (search and queue polls fan out to all enabled ones).
- **Pin manifest per run:** plugin SHAs, Common pin, image tag, log level,
  concurrency settings.
- **Metrics:** search latency p50/p95 idle-vs-load **plus per-sample
  success/empty/timeout counts** (starvation may present as failures, not
  latency); RSS sync wall; queue endpoint response time; 429 + adaptation
  counts via Phase-0 instrumentation; observed peak concurrent in-flight
  tracks (run fails if below threshold — proves the load existed);
  grab→download-complete (plugin-attributable) split from
  download-complete→import (Lidarr-attributable, report-only).

## Phase 2 — Rate-limit correctness + (gated) QoS lanes

Realistically 4 concerns, each its own PR:

- **2a. qobuz search gating (correctness, ungated by Phase 1):** route
  `QobuzIndexer` search traffic through the limiter — either via the bridge
  client or direct `WaitIfNeededAsync` at the call site — and fix the false
  "handled centrally" comment. Without this, no limiter-side QoS can ever
  protect qobuz search.
- **2b. Endpoint-key consolidation in qobuzarr (correctness):** one canonical
  key builder shared by handler and direct call sites.
- **2c. TimeProvider seam in the limiter (prerequisite refactor):** the
  limiter uses raw `DateTime.UtcNow` + `Task.Delay`; deterministic tests
  require injecting `TimeProvider`. Separate, behavior-preserving PR.
- **2d. QoS priority lanes (gated on Phase-0 evidence):** this is a
  **queuing-structure redesign, not a parameter addition**. The current
  implementation irrevocably claims future timestamp slots
  (`limit.LastRequest = nextSlot` under the semaphore,
  `UniversalAdaptiveRateLimiter.cs:308-315`); already-sleeping waiters cannot
  be reordered. Design: parked-waiter dispatcher per bucket — waiters enqueue
  (priority, arrival) and a dispatcher assigns each next slot to the
  highest-priority waiter, with aging and a reserve defined as "≥1 slot per N
  dispatches to search lanes when they have waiters" (a percentage is
  ill-defined when the adaptive budget tightens to its floor). Also unify the
  limiter's two sync primitives (semaphore for slot state; `lock(_lock)` for
  budget mutation, which currently races the unsynchronized read).
  - **API:** new overload as a **default interface method** forwarding to the
    3-arg version (precedent: `RecordAuthFailure`), so external implementors
    (`TidalRateLimiter` override, apple/brainarr test fakes, qobuz
    `CommonStubs.cs`) keep compiling. Thread the priority through
    `NamedServiceRateLimiter` in the same PR + a test asserting priority
    survives an adapter-wrapped limiter (it would otherwise be silently
    dropped — the override delegates 3-arg to Inner).
  - **Priority plumbing:** `HttpRequestMessage.Options` works only for traffic
    that traverses the handler. tidalarr's `TidalApiClient` serves both search
    and download metadata, so the entry points (indexer vs orchestrator) set an
    **AsyncLocal ambient priority scope**; the handler and direct call sites
    read it. qobuzarr's direct `WaitIfNeededAsync` call sites pass priority
    explicitly. Parity test: per-plugin (mechanisms differ), asserting
    indexer-entry traffic carries Interactive/Sync and download-entry Bulk.
  - **Unchanged:** 429 tighten, Retry-After, retry budgets, auth-gate.
    Already-dispatched slots are exempt from re-pricing on budget change.
- **Landing:** Common PRs on dedicated branches; re-pins to both plugins are
  **source-changing PRs** (tidalarr override update, qobuz stub sync), not
  mechanical bumps; re-pin will also carry ~5 unrelated Common commits — note
  in PR body. amazonmusicarr/applemusicarr/brainarr re-pin later; DIM keeps
  them green.

## Phase 3 — Verified hotspot fixes (corrected list)

1. **Multi-tier search early termination (CONFIRMED):**
   `QobuzIndexer.cs:226-273` executes every request in every tier sequentially
   with no early break even when an earlier tier yielded releases. Fix with
   TDD; micro-benchmark + live search-latency delta. Coordinate with tidalarr
   `feat/adopt-query-optimizer` (Phase 0.3).
2. **qobuz substring cache (RE-SCOPED):** the O(n) expiry filter is real
   (`QobuzSubstringCache.cs:656-659`, up to 3 scans/miss) but fused with
   matching that scans all entries anyway, and n is bounded by
   `_maxCacheSize` eviction. Measure n and per-lookup cost first; fix only if
   implicated.
3. **GetItems() snapshot (DEMOTED, hazard noted):** materialization is
   O(active-downloads) — small. Both plugins' `GetSnapshot()` runs the
   retention/eviction sweep **as a side-effect**; naive snapshot caching would
   suppress eviction → unbounded growth. Only touch with the sweep explicitly
   decoupled, and only if baselines implicate queue polling.
4. ~~tidalarr sync-over-async init~~ **(REFUTED):** the
   `GetAwaiter().GetResult()` in `TidalLidarrIndexer`/`TidalLidarrDownloadClient`
   is a per-call sync bridge over an already-lazy cached runtime, required by
   Lidarr's sync host contracts. WONTFIX.

## Phase 4 — Re-measure + report

Re-run Phase-1 scenarios with the same instrumented substrate; the "after"
build differs from baseline **only by the change under test** (build from a
branch, not from a drifted main). Publish before/after table (median + IQR);
document limiter priority semantics in Common docs.

## Process constraints

- Gitea primary (192.168.2.59:3001); every wave starts `git fetch gitea` and
  branches off `gitea/main`; no `gh` CLI.
- Common edits committed immediately on a dedicated branch.
- TDD-first; no wave labels in code comments; one concern per PR; adversarial
  review every wave.

## Roadmap (rev 3) — 20 phases

The detailed sections above are authoritative for P0–P9; the old labels map as
noted. P10+ get one-paragraph charters here and a full spec/plan only when
reached (same gate discipline: verify → measure → fix → re-measure). Every
phase: TDD, one concern per PR, adversarial review, lands on Gitea.

**Arc A — qobuzarr/tidalarr rate limiting + hot paths (P0–P9)**

| P | Charter | Maps to |
|---|---|---|
| 0 | Ground truth + unblocking (canary push, prior-art triage, bucket map, characterization tests, limiter observability). **Canary resolved 2026-07-02; limiter observability landed in Common #84 (`f27c3b9`).** | old Phase 0 |
| 1 | Load harness + baselines on dedicated e2e containers; live = second-scale effects only | old Phase 1 |
| 2 | qobuz search gating — route `QobuzIndexer` traffic through the limiter | old 2a |
| 3 | qobuz endpoint-key consolidation — one canonical key builder | old 2b |
| 4 | TimeProvider seam in the limiter (behavior-preserving) | old 2c |
| 5 | QoS per-bucket parked-waiter dispatcher + DIM overload | old 2d |
| 6 | Plugin priority wiring (tidal AsyncLocal scope; qobuz explicit) + source-changing re-pins | old 2d wiring |
| 7 | Multi-tier search early termination (CONFIRMED hotspot) | old Phase 3.1 |
| 8 | Conditional hotspots: substring-cache + GetItems, only if baselines implicate; eviction decoupling hazard applies | old Phase 3.2–3.3 |
| 9 | qobuzarr/tidalarr re-measure + before/after report | old Phase 4 |

**Arc B — Lidarr.Plugin.Common deep perf (P10–P15)**

| P | Charter |
|---|---|
| 10 | Micro-benchmark substrate: stand up BenchmarkDotNet suites in Common's `bench/` for the hot subsystems (cache key-gen, limiter slot math, retry paths); CI-runnable, baseline numbers committed. This is the measuring stick for all of Arc B. |
| 11 | Caching subsystem: `StreamingResponseCache` key-generation allocations, sliding-expiration per-entry lock contention, cleanup sweeps; fix what P10 benchmarks implicate. |
| 12 | HTTP/resilience stack: `HttpClientExtensions` retry/jitter budgets, `GenericResilienceExecutor` per-host gates, handler-chain overhead per request; verify no sync-over-async on hot paths. |
| 13 | Download orchestration + telemetry: `SimpleDownloadOrchestrator` semaphore patterns, `DownloadTelemetryService` hot-path cost, tracker snapshot/eviction costs at scale. |
| 14 | Allocation/GC pass: top allocators from P10–P13 evidence (pooling, Span, cached delegates) — strictly evidence-gated, no speculative micro-optimization. |
| 15 | Test-suite performance + determinism: Common's suite wall time (4m10s today), TimeProvider adoption across time-coupled tests, eliminate residual real-delay tests (the P0 characterization tests get migrated onto the P4 seam). |

**Arc C — brainarr (P16–P18)**

| P | Charter |
|---|---|
| 16 | brainarr perf baseline: recommendation-pipeline wall time, AI-provider call latency/counts, cache effectiveness (RecommendationCache), library-scan cost on the live lidarr-e2e GLM setup. COORDINATE: a concurrent brainarr quality /loop exists — fence zones, no overlapping files. |
| 17 | Provider rate-limit/throttle correctness: brainarr's per-provider limiter semantics (history: AIService rate-limit key-shape bug), retry storms on provider 429/5xx, token-budget behavior under batch recommendation load — same verify-first discipline as P0's bucket map. |
| 18 | brainarr evidenced hotspot fixes (one concern per PR, micro-benchmarks where ms-scale). |

**Arc D — close-out (P19)**

| P | Charter |
|---|---|
| 19 | Ecosystem re-measure: re-run all baselines on final SHAs; publish the consolidated before/after report; document limiter QoS + perf invariants in Common docs and per-plugin wikis; file follow-ups that didn't make the bar. |

Sequencing: arcs are largely serial (A → B → C → D) but P10 (bench substrate)
can start as soon as P4 lands, and Arc C is independent of Arc A after P5/P6.
Re-pin waves propagate Common changes to amazonmusicarr/applemusicarr/brainarr
at arc boundaries, kept green by the DIM rule.

## Review record (rev 1 → rev 2)

3-lens adversarial review (limiter design / measurement methodology /
process+integration). Accepted, all independently verified against code:
per-endpoint not per-service budgets; qobuz search bypasses limiter; slot-claim
structure can't take priorities without a dispatcher rewrite; TidalRateLimiter
override breaks naive interface change (→ DIM); limiter observability was
missing at review time and later landed in Common #84 (`f27c3b9`);
real-account volume caps; reset/repeatability/isolation requirements;
prior-art branch collisions; Phase-3 list corrections (1 confirmed, 1
re-scoped, 1 demoted, 1 refuted). Rejected: none — every BLOCKER/MAJOR
withstood spot verification (`QobuzIndexer.cs:236`,
`UniversalAdaptiveRateLimiter.cs:308-315`, `TidalRateLimiter.cs:22`,
`ls-remote` branch evidence).
