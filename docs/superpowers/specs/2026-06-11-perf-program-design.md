# qobuzarr / tidalarr Performance Program — Design

**Date:** 2026-06-11
**Scope:** qobuzarr, tidalarr, Lidarr.Plugin.Common
**Driver:** Proactive performance sweep, with rate-limit behavior under parallel load
(heavy downloads + concurrent indexer searches) as the first-class concern.

## Problem statement

Both plugins gate all upstream API traffic through a single shared
`UniversalAdaptiveRateLimiter` (adaptive token bucket, per-service/per-endpoint).
This correctly prevents search and download traffic from jointly overrunning the
Qobuz/Tidal APIs, but the limiter has **no fairness or priority between caller
paths**: under heavy download load (e.g. 10 parallel album grabs on tidalarr ≈ up
to ~40 concurrent chunk/metadata fetches), bulk download traffic can monopolize
the budget and starve RSS sync and interactive search until the queue drains.

Separately, an exploration sweep surfaced perf-hotspot candidates (download-tracker
materialization in `GetItems()`, O(n) substring-cache scans, sequential multi-tier
search without early termination, sync-over-async plugin init). Historical
defect-hunt false-positive rate in this ecosystem is ~50–60%, so **no hotspot is
fixed without prior verification and a measured baseline implicating it**.

## Goals / success criteria

1. Interactive search and RSS sync remain responsive (bounded added latency)
   while the download queue is saturated — demonstrated on the live `lidarr-e2e`
   instance with before/after numbers.
2. No regression in API safety: total request rate to upstream APIs never exceeds
   today's adaptive budget; 429 handling, Retry-After, and auth-gate behavior
   unchanged.
3. Each shipped perf fix carries a measured before/after delta from the same
   harness scenario.
4. All changes TDD'd, one concern per PR, landed on Gitea, adversarially reviewed.

## Non-goals

- Newtonsoft→System.Text.Json migration in qobuzarr (large blast radius,
  single-digit-ms expected gain) — deferred unless the baseline implicates it.
- Sliding-expiration lock redesign in `StreamingResponseCache` — deferred, same rule.
- amazonmusicarr / applemusicarr / brainarr (out of scope; QoS limiter lands in
  Common so they inherit it on their next re-pin).

## Phase 1 — Load harness + baseline

A scripted harness at `C:\r\Alex\github\.perf-harness\` (sibling of `.glm-wave`,
spans repos, not itself a git repo) driving the live `lidarr-e2e` container on
`:8787` via Lidarr's API.

**Load generator:** queue N parallel album grabs (default 10) while concurrently
firing RSS sync and interactive artist/album searches on a fixed cadence.

**Metrics per run:**
- interactive search latency p50/p95, idle vs under load
- RSS sync wall time, idle vs under load
- time-to-first-search-result while downloads saturate
- grab-to-import wall time per album
- queue endpoint (`GetItems`) response time distribution
- 429 count + limiter adaptation events (parsed from Lidarr logs)
- container CPU/memory over the run

**Scenarios:** one per plugin; tidalarr additionally at elevated
`MaxConcurrentTrackDownloads` to provoke the starvation case.

**Output:** timestamped markdown report per run. Every later fix re-runs the same
scenario for its delta.

## Phase 2 — QoS priority lanes in Common's rate limiter

Extend `UniversalAdaptiveRateLimiter` with priority lanes **sharing the existing
single per-service budget** (no second bucket — joint-overrun protection intact).

- **Priorities:** `Interactive` (manual search) > `Sync` (RSS) > `Bulk`
  (downloads). `WaitIfNeededAsync` gains a priority parameter defaulting to
  `Bulk`, so existing callers compile and behave unchanged.
- **Allocation rule:** when pacing slots free, higher-priority waiters are served
  first, with a **reserved minimum share for search lanes** (initially 20% of the
  budget whenever search has waiters). Bulk may use 100% of idle capacity; aging
  prevents bulk starvation in the inverse direction.
- **Wiring:** indexer request paths tag requests via `HttpRequestMessage.Options`;
  `AdaptiveRateLimitingHandler` reads the tag and forwards the priority. Download
  clients require no changes (default = `Bulk`).
- **Unchanged:** 429 tighten-on-hit, Retry-After waits, retry budgets, auth-gate.
  Priorities only reorder who waits, never how much total is sent.
- **Testing:** deterministic unit tests against a fake `TimeProvider` proving
  "saturating bulk traffic + arriving Interactive request ⇒ served within bounded
  delay" and "no bulk starvation under sustained search load." A parity test
  guards that both plugins tag their indexer paths.
- **Landing:** Common PR on this dedicated branch (committed immediately, per the
  Common-volatility rule), then both plugins re-pinned per the documented
  submodule re-pin mechanics.

## Phase 3 — Verified hotspot fixes (gated on baseline evidence)

Candidates in rank order; each gets claim verification + baseline implication
before any fix, then TDD, then its own PR:

1. `GetItems()` materializes the full download tracker per poll → versioned/cached
   snapshot invalidated on tracker mutation.
2. qobuzarr substring cache O(n) full scans per lookup → expiry index
   (sorted expiry queue, walk only entries due).
3. Multi-tier search chains run all tiers sequentially → early termination when a
   tier yields sufficient confident results.
4. tidalarr indexer/download-client sync-over-async init
   (`GetAwaiter().GetResult()` at construction) → lazy async init on first call.

## Phase 4 — Re-measure + report

Re-run Phase-1 scenarios, publish before/after table, document the limiter's
priority semantics in Common docs. Adversarial review before each phase lands.

## Process constraints

- Gitea is primary (192.168.2.59:3001); branch off `gitea/main`; no `gh` CLI.
- Common edits committed immediately on a dedicated branch.
- TDD-first for all substantive changes; no wave labels in code comments.
- One concern per PR; adversarial review every wave.
