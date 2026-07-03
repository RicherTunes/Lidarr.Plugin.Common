# Perf Program Phase 1 — Load Harness + Baselines: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans.
> Companion to the rev-3 spec (P1) and the Phase-0 evidence doc, which fixes the
> metrics and the instrumentation log shapes this harness parses.

**Goal:** A reusable PowerShell harness at `C:\r\Alex\github\.perf-harness\` that
runs the spec's load scenario against a Lidarr instance and emits a
timestamped markdown baseline report.

**Architecture:** Pure REST-API driver (no docker-compose ownership): the
target instance is a parameter (`-BaseUrl`, `-ApiKey`, optional
`-ContainerName` for restart/log capture via `docker`). Three files: an API
module, a metrics module, an orchestrator script. Reports land in
`.perf-harness\reports\`.

**Tech Stack:** PowerShell 7, Lidarr v1 REST API, docker CLI for restart +
`docker logs` capture.

---

### File structure

- `C:\r\Alex\github\.perf-harness\lib\LidarrApi.psm1` — thin typed wrappers:
  system status, indexer/downloadclient enable-disable, interactive album
  search (`GET /api/v1/release?albumId=`), RSS sync command + completion poll,
  release grab, queue snapshot (timed), blocklist clear, trackfile delete,
  history query.
- `C:\r\Alex\github\.perf-harness\lib\Metrics.psm1` — timing sampler
  (captures per-call latency + outcome success/empty/timeout/error),
  percentile math (p50/p95 + IQR over reps), docker-log parser for the
  Phase-0 instrumentation lines (`rate-limit budget adapted {old} -> {new}`,
  `limiter stats:`, plus 429 Retry-After warnings).
- `C:\r\Alex\github\.perf-harness\run-scenario.ps1` — orchestrator (see
  procedure below).
- `C:\r\Alex\github\.perf-harness\corpus\<plugin>.json` — pinned album corpus
  (musicbrainz album ids + expected qobuz/tidal hits), pre-validated to import
  cleanly. Populated in the corpus-validation step, NOT hand-invented.
- `C:\r\Alex\github\.perf-harness\reports\` — output (gitignored if this dir
  ever becomes a repo; it is plain tooling for now).

### Scenario procedure (one run; the orchestrator loops `-Reps` times)

1. **Pin manifest:** record plugin SHAs (`git -C <repo> rev-parse HEAD`),
   Common pin (`git submodule status`), instance version, settings
   (concurrency), corpus hash → report header.
2. **Reset:** clear queue (`DELETE /api/v1/queue/{id}?removeFromClient=true`),
   clear blocklist, delete corpus trackfiles, `docker restart` (resets
   in-memory limiter/caches), poll `/api/v1/system/status` until ready,
   discard one warm-up search.
3. **Isolation check:** assert the non-measured plugin's indexer + download
   client are disabled (fail the run otherwise).
4. **Idle sampling:** K=10 interactive searches over the corpus on a 5 s
   cadence → idle latency distribution + outcome counts.
5. **Load:** grab N=2–3 corpus albums back-to-back; immediately resume
   interactive search sampling on the same cadence + trigger one RSS sync.
   Record: per-search latency/outcome, RSS sync wall (command poll), timed
   queue snapshots every 5 s (the `GetItems` proxy), peak concurrent
   in-flight tracks (fail the run below threshold ≥2).
6. **Drain:** wait for queue empty/import or 15 min ceiling; record per-album
   grab→download-complete and download-complete→import splits (from history +
   queue transitions).
7. **Collect:** `docker logs --since <run-start>` → parse 429s, budget
   adaptations, limiter stats lines.
8. **Report:** append per-rep rows; after all reps emit median + IQR per
   metric, idle vs load, with the acceptance rule from the spec (delta must
   exceed run-to-run spread to count).

### Acceptance gates for the harness itself

- [ ] Smoke: every `LidarrApi.psm1` function exercised read-only against a
      live instance (GET-only smoke must not grab/delete).
- [ ] One full dry scenario rep against a dedicated container with the
      non-measured plugin disabled, 2-album corpus, verifying the report
      renders and the log parser finds the Phase-0 instrumentation lines
      (requires the observability change deployed to that container — hence
      baselines wait on a container rebuild after Common #84).
- [ ] Corpus validation: each corpus album imports cleanly once (edition
      match), else replaced — this is what makes grab→import a usable metric.

### Explicit non-goals (v1)

- No container provisioning (reuse existing e2e container tooling; pass
  `-BaseUrl/-ApiKey/-ContainerName`).
- No ms-scale metrics (micro-benchmarks own those, P10).
- Live runs capped: 2–3 albums, lowest quality settings, ≥5 reps; the
  10-album stress shot is a single end-of-arc validation.

### Sequencing note

Baselines (the actual P1 deliverable) additionally require, in order:
1. Gitea canary green (resolved 2026-07-02; keep Gitea as the primary gate).
2. `fix/response-cache-endpoint-matching` (qobuz) + `feat/adopt-query-optimizer`
   (tidal) landed (LAND-BEFORE-BASELINE verdicts).
3. Limiter observability landed in Common (#84, `f27c3b9`) + plugins re-pinned + container image
   rebuilt (so the log parser has its instrumentation).
Harness construction and smoke (gates 1–2 above minus instrumentation
assertions) proceed now, locally.
