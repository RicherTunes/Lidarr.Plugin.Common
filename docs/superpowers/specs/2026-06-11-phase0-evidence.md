# Perf Program — Phase 0 Evidence

Date: 2026-06-11. Companion to `2026-06-11-perf-program-design.md` (rev 2).

## Gitea canary (Task 1) — RESOLVED 2026-07-02

Push attempt 2026-06-11 failed with the server's own diagnosis:
`remote: fatal: unable to write loose object file: No space left on device`.
The 2026-06-10 "unpacker error" suspicion is confirmed: the Gitea data volume
on 192.168.2.59 was full. As of 2026-07-02, Gitea accepts pushes again and
program work should proceed through normal Gitea PRs with CI as the merge gate.

## Prior-art triage (Task 2)

| Branch | Tip | Verdict | Rationale |
|---|---|---|---|
| `perf/ratelimiter-lane` | 9d1e8f3 | **CLOSE** | Two-dot diff vs `gitea/main` on `UniversalAdaptiveRateLimiter.cs` is EMPTY — the slot-claim redesign it introduces is already on main (squash-merged; stale ref). Delete the stale branch when convenient. |
| `refactor/adaptive-ratelimit-dedup` | 5d82071 | **LAND after rebase/review** | Live -32 LOC cleanup: handler delegates `BuildEndpointKey`/`ResolveRetryAfter` to `RateLimitHeaderUtilities` (which already exists on main with tests). No behavior change. Rebase after the observability PR because both touch `AdaptiveRateLimitingHandler.cs`. |

## Plugin in-flight branches (Task 3)

| Branch | Tip | Path affected | Verdict |
|---|---|---|---|
| qobuzarr `fix/response-cache-endpoint-matching` | 2a54155 | SEARCH (fixes cache predicate so search/metadata actually cache; guards short-lived `track/getFileUrl` URLs from miscaching) | **LAND-BEFORE-BASELINE** — landing mid-program flips search from uncached to cached and corrupts every delta |
| tidalarr `feat/adopt-query-optimizer` | a5b9e9c | SEARCH (lights up optimize→search→learn loop; primary path = whitespace normalization only, tests pin invariant) | **LAND-BEFORE-BASELINE** |
| tidalarr `fix/tidal-snapshot-field-drop` | fc9895f | DOWNLOAD settings hydration (lyrics flags) | **PARK** — orthogonal to perf metrics |

Baseline reports must record the exact SHAs of all three repos + Common pin.

## Bucket map (Task 4)

Bucket key for handler-gated traffic = `host:firstPathSegment`
(`AdaptiveRateLimitingHandler.cs:138-145`).

### tidalarr — ONE shared bucket, and it's the big one

Every `api.tidal.com/v1/...` URI has first path segment `v1`, so **search,
album metadata, track metadata, and playbackinfo ALL share bucket
`api.tidal.com:v1`**:

| Traffic | Client | Example URI | Bucket | Shared w/ search? |
|---|---|---|---|---|
| Search (indexer) | TidalApiClient | `api.tidal.com/v1/search?...` | `api.tidal.com:v1` | — |
| Album/track metadata (download) | TidalApiClient (orchestrator delegates, `TidalModule.cs:390-398`) | `api.tidal.com/v1/albums/123`, `/v1/tracks/456`, `/v1/tracks/456/playbackinfo...` | `api.tidal.com:v1` | **YES** |
| OAuth refresh | TidalOAuthService | `auth.tidal.com/v1/oauth2/token` | `auth.tidal.com:v1` | no |
| Chunk fetches | TidalChunkDownloader | `sp-ap-eu.audio.tidal.com/...` | `sp-ap-eu.audio.tidal.com:` | no |

Load arithmetic for a 10-album (~12 tracks/album) parallel grab: ~10 getAlbum
+ ~10 getAlbumTracks + ~120 getStreamInfo = **~140 calls into
`api.tidal.com:v1`**. The limiter claims FIFO future slots irrevocably
(`UniversalAdaptiveRateLimiter.cs:308-315`); at Tidal's default 300 RPM
(200 ms/slot), 140 queued slots put an arriving search call **~28 s** behind
the burst. This is the starvation mechanism, now code-proven at the correct
scope (rev-1's "service-wide budget" wording was wrong; rev-2 + this map fix it).

### qobuzarr — NO shared bucket; search is unmetered

| Traffic | Client | Bucket key | Limiter-gated? |
|---|---|---|---|
| Search (indexer) | **Lidarr host IHttpClient** (`QobuzIndexer.cs:236`) | n/a | **NO — bypasses the limiter entirely** |
| Download-side album search | AdaptiveQobuzApiClient via `LidarrAlbumRetriever.cs:172` | literal `"/album/search"` / bare `"/catalog/search"` | yes (unstable keys) |
| Streaming URL fetch | AdaptiveQobuzApiClient (`:143,169`) | bare `"/track/getFileUrl"` | yes (unstable keys) |
| Bridge metadata | BridgeQobuzApiClient | n/a | **NO** (no WaitIfNeededAsync; registered with the handler but direct GetAsync paths found without it — verify in 2a) |

Direct call sites pass full-URL-with-query keys in some paths (each query =
fresh bucket → pacing barely engages, unbounded cardinality) and bare paths in
others. Confirms spec Phase 2a (gate qobuz search) + 2b (one canonical key
builder) as correctness prerequisites — limiter-side QoS cannot help qobuz
until then.

## Gate decision (Task 7, provisional pending live confirmation)

**Phase 2d (priority lanes): PROCEED, scoped per-bucket.** The contention is
real, mechanical, and localized to `api.tidal.com:v1` (and to any future
single-host API bucket — qobuz will join once 2a/2b land, since
`www.qobuz.com/api.json/...` likewise yields one bucket `www.qobuz.com:api.json`).
Because contention is within ONE bucket, the dispatcher only needs to
prioritize waiters per-bucket — no cross-bucket scheduler. Phase 1's live run
confirms user-visible magnitude and provides the before/after headline, but
the design no longer hangs on it.

Order of operations stands: 2a/2b (correctness) and 2c (TimeProvider seam)
first; 2d dispatcher after; Gitea is no longer a landing blocker.
