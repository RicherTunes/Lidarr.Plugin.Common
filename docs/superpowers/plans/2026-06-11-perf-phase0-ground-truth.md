# Perf Program Phase 0 — Ground Truth + Unblocking: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish the verified facts that gate the rest of the perf program: whether Gitea can accept pushes, what prior-art branches must be triaged, which buckets search/download traffic actually share, and limiter observability for the Phase-1 harness.

**Architecture:** No production behavior changes except handler-level observability logging (Task 5). Evidence tasks produce a committed evidence doc; characterization tests pin current limiter queuing semantics as executable documentation.

**Tech Stack:** .NET / C# (xUnit, file-scoped namespaces), PowerShell, git worktrees. Spec: `docs/superpowers/specs/2026-06-11-perf-program-design.md` (rev 2).

**Repos:** `lidarr.plugin.common` (Common), `qobuzarr`, `tidalarr`. All branches off `gitea/main` after `git fetch gitea`. Gitea pushes are currently FAILING (server disk full) — commit locally; push when unblocked.

---

### Task 1: Gitea canary push (BLOCKED on server disk space — user action)

**Files:** none (git only)

- [ ] **Step 1: After the user frees disk on 192.168.2.59, retry the canary**

Run: `git -C C:\r\Alex\github\.claude-work\common-perf-spec push -u gitea docs/perf-program-2026-06`
Expected: branch accepted. (2026-06-11 attempt failed: `remote: fatal: unable to write loose object file: No space left on device`.)

- [ ] **Step 2: Verify**

Run: `git -C C:\r\Alex\github\lidarr.plugin.common ls-remote --heads gitea docs/perf-program-2026-06`
Expected: one ref line. Until this passes, ALL work in this program stays local-only.

### Task 2: Prior-art triage — Common branches on the limiter files

**Files:**
- Create: `docs/superpowers/specs/2026-06-11-phase0-evidence.md` (section "Prior-art triage", in the spec worktree)

- [ ] **Step 1: Fetch and inspect both branches**

```powershell
git -C C:\r\Alex\github\lidarr.plugin.common fetch gitea
git -C C:\r\Alex\github\lidarr.plugin.common log --oneline gitea/main..gitea/perf/ratelimiter-lane
git -C C:\r\Alex\github\lidarr.plugin.common diff gitea/main...gitea/perf/ratelimiter-lane -- src/Services/Performance/UniversalAdaptiveRateLimiter.cs
git -C C:\r\Alex\github\lidarr.plugin.common log --oneline gitea/main..gitea/refactor/adaptive-ratelimit-dedup
git -C C:\r\Alex\github\lidarr.plugin.common diff gitea/main...gitea/refactor/adaptive-ratelimit-dedup -- src/Services/Http/AdaptiveRateLimitingHandler.cs
```

- [ ] **Step 2: For each branch, decide LAND / SUPERSEDE / CLOSE**

Decision criteria: Is it already merged in spirit (diff vs main empty/equivalent)? Does it implement part of Phase 2d (fairness)? Does it conflict with Task 5's handler edits? Record per-branch: tip SHA, summary of diff, decision, rationale.

- [ ] **Step 3: Write the "Prior-art triage" section of the evidence doc and commit**

```powershell
git -C C:\r\Alex\github\.claude-work\common-perf-spec add docs/superpowers/specs/2026-06-11-phase0-evidence.md
git -C C:\r\Alex\github\.claude-work\common-perf-spec commit -m "docs: phase-0 evidence — prior-art triage"
```

### Task 3: In-flight plugin branch triage

**Files:**
- Modify: `docs/superpowers/specs/2026-06-11-phase0-evidence.md` (section "Plugin in-flight branches")

- [ ] **Step 1: Inspect the three branches**

```powershell
git -C C:\r\Alex\github\qobuzarr fetch gitea
git -C C:\r\Alex\github\qobuzarr log --oneline gitea/main..gitea/fix/response-cache-endpoint-matching
git -C C:\r\Alex\github\qobuzarr diff --stat gitea/main...gitea/fix/response-cache-endpoint-matching
git -C C:\r\Alex\github\tidalarr fetch gitea
git -C C:\r\Alex\github\tidalarr log --oneline gitea/main..gitea/feat/adopt-query-optimizer
git -C C:\r\Alex\github\tidalarr log --oneline gitea/main..gitea/fix/tidal-snapshot-field-drop
```

- [ ] **Step 2: Decide LAND-BEFORE-BASELINE / PARK for each; record in evidence doc; commit**

Rule from the spec: anything that changes search-path caching or search behavior must land (or be explicitly parked/closed) before a Phase-1 baseline is captured, and baseline reports must record the SHAs used.

### Task 4: Bucket map — which endpoint buckets do search and download metadata share?

**Files:**
- Modify: `docs/superpowers/specs/2026-06-11-phase0-evidence.md` (section "Bucket map")
- Read-only inputs: `tidalarr/src/Tidalarr/**` (TidalApiClient request URIs, orchestrator delegates in `TidalModule.cs:390-398`), `lidarr.plugin.common/src/Services/Http/AdaptiveRateLimitingHandler.cs:138-145` (key = `host:firstPathSegment`), qobuzarr direct call sites (`QobuzHttpClient.cs:71`, `AdaptiveQobuzApiClient.cs`, `LidarrAlbumRetriever.cs:172`)

- [ ] **Step 1: Enumerate every URI each client issues, derive its bucket key**

For tidalarr: search path (indexer → TidalApiClient), download metadata path (orchestrator getAlbum/getTrack/playbackinfo → same TidalApiClient?), OAuth, chunk fetches. For qobuzarr: handler-gated bridge traffic vs direct `WaitIfNeededAsync` callers and their three keying schemes.

- [ ] **Step 2: Produce the bucket table**

Columns: traffic class | client | example URI | bucket key | shared with search? For each shared bucket, note expected req/min from each side under the 10-album scenario.

- [ ] **Step 3: Write the gate conclusion**

One paragraph: does a shared bucket exist where bulk metadata traffic can queue ahead of search (yes/no, which), and does qobuz search bypass mean the limiter cannot be the qobuz bottleneck (expected: yes). This paragraph IS the Phase-2d gate input. Commit the evidence doc.

### Task 5: Characterization tests — pin current within-bucket queuing + cross-bucket independence

**Files:**
- Create: `tests/Services/Performance/UniversalAdaptiveRateLimiterQueuingCharacterizationTests.cs` (new worktree `C:\r\Alex\github\.claude-work\common-limiter-chartests`, branch `test/limiter-queuing-characterization` off `gitea/main`)

- [ ] **Step 1: Create the worktree**

```powershell
git -C C:\r\Alex\github\lidarr.plugin.common worktree add C:\r\Alex\github\.claude-work\common-limiter-chartests -b test/limiter-queuing-characterization gitea/main
```

- [ ] **Step 2: Write the tests (they should PASS — they pin CURRENT behavior)**

```csharp
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

using Lidarr.Plugin.Common.Services.Performance;

using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Performance;

/// <summary>
/// Characterization of the limiter's slot-claim queuing (perf-program Phase 0).
/// Pins two facts the QoS design depends on:
/// 1. Within one endpoint bucket, a burst of waiters claims future slots
///    irrevocably, so a later arrival waits behind the whole burst (the
///    starvation mechanism — there is no priority or reordering).
/// 2. Different endpoint buckets do not contend (per-endpoint pacing).
/// Bounds are generous one-sided limits: slot math is exact and Task.Delay
/// only ever adds latency, so the lower bound cannot flake.
/// </summary>
public sealed class UniversalAdaptiveRateLimiterQueuingCharacterizationTests
{
    private const string Service = "Qobuz"; // default config: 500 RPM => 120 ms/slot
    private const double SlotMs = 60000.0 / 500;

    [Fact]
    public async Task SameBucket_BurstClaimsSlots_LaterArrivalWaitsBehindEntireBurst()
    {
        using var limiter = new UniversalAdaptiveRateLimiter();

        // 10 concurrent waiters claim slots t0, t0+120ms, ..., t0+1080ms.
        var burst = Enumerable.Range(0, 10)
            .Select(_ => limiter.WaitIfNeededAsync(Service, "albums"))
            .ToArray();
        await Task.Delay(100); // let every burst caller pass the claim point

        var sw = Stopwatch.StartNew();
        await limiter.WaitIfNeededAsync(Service, "albums");
        sw.Stop();
        await Task.WhenAll(burst);

        // 11th arrival's slot is >= t0 + 10*120ms; 100ms already elapsed.
        // Generous bound: at least half the theoretical 1100ms remainder.
        Assert.True(sw.ElapsedMilliseconds >= 5 * SlotMs,
            $"Later same-bucket arrival should wait behind the burst; waited {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task DifferentBucket_BurstDoesNotDelayOtherEndpoint()
    {
        using var limiter = new UniversalAdaptiveRateLimiter();

        var burst = Enumerable.Range(0, 10)
            .Select(_ => limiter.WaitIfNeededAsync(Service, "albums"))
            .ToArray();
        await Task.Delay(100);

        var sw = Stopwatch.StartNew();
        await limiter.WaitIfNeededAsync(Service, "search"); // different bucket
        sw.Stop();
        await Task.WhenAll(burst);

        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"Different bucket must not queue behind the burst; waited {sw.ElapsedMilliseconds} ms");
    }
}
```

- [ ] **Step 3: Run them**

Run: `dotnet test C:\r\Alex\github\.claude-work\common-limiter-chartests\tests --filter "FullyQualifiedName~QueuingCharacterization" -v minimal`
Expected: 2 passed. If the same-bucket test FAILS, the spec's residual starvation mechanism is wrong too — stop and re-evaluate before Phase 2d.

- [ ] **Step 4: Commit**

```powershell
git -C C:\r\Alex\github\.claude-work\common-limiter-chartests add tests/Services/Performance/UniversalAdaptiveRateLimiterQueuingCharacterizationTests.cs
git -C C:\r\Alex\github\.claude-work\common-limiter-chartests commit -m "test: characterize limiter slot-claim queuing (within-bucket FIFO, cross-bucket independence)"
```

### Task 6: Handler-level observability — adaptation + periodic stats logging (TDD)

The limiter itself logs nothing and its stats surface is unread. Lowest-risk fix: log from `AdaptiveRateLimitingHandler`, which already has the optional `ILogger` and brackets every gated request. Limitation (accepted for Phase 0): qobuzarr's direct `WaitIfNeededAsync`/`RecordResponse` call sites are not covered — they are being consolidated in Phase 2b anyway.

**Files:**
- Modify: `src/Services/Http/AdaptiveRateLimitingHandler.cs` (new worktree `C:\r\Alex\github\.claude-work\common-limiter-obs`, branch `feat/limiter-observability` off `gitea/main` — AFTER Task 2 decides the fate of `refactor/adaptive-ratelimit-dedup`, which edits this file)
- Test: `tests/Services/Http/AdaptiveRateLimitingHandlerObservabilityTests.cs`

- [ ] **Step 1: Create worktree (after Task 2 triage decision)**

```powershell
git -C C:\r\Alex\github\lidarr.plugin.common worktree add C:\r\Alex\github\.claude-work\common-limiter-obs -b feat/limiter-observability gitea/main
```

- [ ] **Step 2: Write the failing test**

```csharp
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Services.Performance;

using Microsoft.Extensions.Logging;

using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Http;

public sealed class AdaptiveRateLimitingHandlerObservabilityTests
{
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add($"{logLevel}|{formatter(state, exception)}");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public StubHandler(HttpStatusCode status) => _status = status;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status));
    }

    [Fact]
    public async Task TooManyRequests_LogsAdaptationWithOldAndNewBudget()
    {
        var logger = new ListLogger<AdaptiveRateLimitingHandler>();
        using var limiter = new UniversalAdaptiveRateLimiter();
        using var handler = new TestableHandler(limiter, "Qobuz", logger)
        {
            InnerHandler = new StubHandler(HttpStatusCode.TooManyRequests),
        };
        using var client = new HttpClient(handler);

        await client.GetAsync("https://api.example.com/albums/123");

        // Qobuz default 500 RPM tightens to 375 on a 429.
        Assert.Contains(logger.Lines, l =>
            l.Contains("rate-limit budget adapted") &&
            l.Contains("500") && l.Contains("375"));
    }

    private sealed class TestableHandler : AdaptiveRateLimitingHandler
    {
        public TestableHandler(IUniversalAdaptiveRateLimiter limiter, string service,
            ILogger<AdaptiveRateLimitingHandler> logger)
            : base(limiter, service, logger) { }
    }
}
```

(Adjust the `TestableHandler` shim to the handler's real ctor accessibility — it is the base of per-plugin subclasses, so a protected ctor is expected; mirror whatever `QobuzRateLimitingHandler` does.)

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test C:\r\Alex\github\.claude-work\common-limiter-obs\tests --filter "FullyQualifiedName~HandlerObservability" -v minimal`
Expected: FAIL (no adaptation log line exists yet).

- [ ] **Step 4: Implement in `AdaptiveRateLimitingHandler.SendAsync`**

Around the existing `RecordResponse` call (currently ~line 97), capture the budget before/after and log on change; also log a stats summary every 100 responses:

```csharp
var oldRpm = _limiter.GetCurrentLimit(_serviceName, endpointKey);
_limiter.RecordResponse(_serviceName, endpointKey, response);
var newRpm = _limiter.GetCurrentLimit(_serviceName, endpointKey);

if (newRpm != oldRpm)
{
    _logger?.LogInformation(
        "{Service}/{Endpoint} rate-limit budget adapted {OldRpm} -> {NewRpm} (status {Status})",
        _serviceName, endpointKey, oldRpm, newRpm, (int)response.StatusCode);
}

if (Interlocked.Increment(ref _responseCount) % 100 == 0)
{
    var stats = _limiter.GetServiceStats(_serviceName);
    _logger?.LogInformation(
        "{Service} limiter stats: {Requests} req, {Errors} err, {RateLimitHits} 429s",
        _serviceName, stats.TotalRequests, stats.TotalErrors, stats.TotalRateLimitHits);
}
```

Add the field: `private long _responseCount;`. Match the file's existing null-logger convention.

- [ ] **Step 5: Run tests to verify pass, then run the full Common suite**

Run: `dotnet test C:\r\Alex\github\.claude-work\common-limiter-obs\tests -v minimal`
Expected: all green (this is the no-regression gate).

- [ ] **Step 6: Commit**

```powershell
git -C C:\r\Alex\github\.claude-work\common-limiter-obs add -A
git -C C:\r\Alex\github\.claude-work\common-limiter-obs commit -m "feat(http): log rate-limit budget adaptations and periodic limiter stats"
```

### Task 7: Phase-0 wrap — gate decision + next-phase plans

**Files:**
- Modify: `docs/superpowers/specs/2026-06-11-phase0-evidence.md` (section "Gate decision")

- [ ] **Step 1: Write the gate decision** — combining Task 4's bucket map and Task 5's pinned queuing behavior: proceed / re-scope / drop Phase 2d, and what the Phase-1 harness must demonstrate live.
- [ ] **Step 2: Write the Phase-1 (harness) implementation plan as a new doc** following the rev-2 spec's Phase-1 section, parameterized by the evidence (which buckets to watch, which log lines to parse — now defined by Task 6's format).
- [ ] **Step 3: Commit; push everything once Task 1 unblocks.**

---

## Execution notes

- Order: Task 2 → (3, 4, 5 in parallel) → 6 → 7. Task 1 fires whenever the user frees server disk.
- Tasks 2/3/4 are read-only research — safe for parallel subagents. Tasks 5/6 are small TDD code tasks in isolated worktrees.
- Everything commits locally; NOTHING pushes until Task 1's canary passes.
