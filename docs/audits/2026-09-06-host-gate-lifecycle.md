# Host-gate lifecycle hardening — 2026-09-06

## Integration boundary

Candidate branch: `fix/host-gate-lifecycle-20260906`.
Base: Gitea Common main `e09f7791d1357b5316817b9fd9c5616e28e0704f`.
Worktree: `C:/Users/Alexandre/.devspace/worktrees/common-bbaa93de`.

The user is merging concurrently. This work is intentionally isolated: no main/master merge, consumer re-pin, branch deletion, reset of an original checkout, or protection change is part of this candidate. It is Common source acceptance, not a completed five-plugin rollout. Remote required checks, coordinated consumer adoption and fresh packaging/coexistence verification remain integration gates.

## Reproduced defect

`Clear` and `Shutdown` disposed registry semaphores even while requests or waiters retained them. Six deterministic cases reproduced a successful send turning into `ObjectDisposedException` during permit release: generic execution, default typed HTTP, and supplied-clock HTTP, each with clear and shutdown. Idle sweeping also previously decided removal outside the lookup lock, leaving a lookup-before-wait race.

The first test build stopped during .NET compiler startup; the next serial build ran the tests and produced the six actual behavioral failures. The compiler-startup failure is not counted as a regression red.

## Shared ownership repair

`HostGateRegistry.Reserve` registers ownership before any asynchronous wait; pair reservation is atomic. A reservation covers queued acquisition as well as active use. `HostGateLease` owns the reservation and releases acquired permits before releasing registry references, including partial-acquisition failure and cancellation.

Retirement retains the existing gate while references remain. New same-key work joins the draining gate instead of obtaining an independent concurrency limit. The last return removes and disposes an eligible retired entry. Shutdown remains nonblocking and stops the sweeper; future use can rearm it. It does not forcibly terminate noncooperative work.

Dictionary membership, limit growth, reference changes, retirement and disposal share a short metadata lock. The registry never holds that lock while waiting for permits or performing HTTP work. Idle age uses monotonic timestamps and restarts on the last return. Sweeper callbacks carry a generation identity checked under the same lock, so an earlier queued callback cannot sweep a reinitialized generation.

Generic execution, both typed HTTP cores, and both cross-host redirect branches use key-based leases. A production-source search found no remaining `HostGateRegistry.Get` callers. The raw semaphore APIs remain for existing diagnostics/tests only; an unreserved raw reference is not a lifecycle-safe production handle.

No public signatures, dependencies, host requirements, plugin settings or configured concurrency limits change. Existing upward limit growth and cancellation/timeout behavior are preserved. The registry now uses ordinary dictionaries under its ownership lock rather than combining that lock with concurrent dictionaries and nested state locks.

## Completed local evidence

| Check | Result |
| --- | --- |
| Initial behavioral regression tests | 6 failures before production changes |
| Final new lifecycle tests | 28 passed, 0 failed, 0 skipped |
| Repeated lifecycle runs | 10 runs, 280 passes, 0 failures |
| Affected HTTP/resilience/ownership suite | 195 passed, 0 failed, 0 skipped |
| Full Common default suite | 7,533 passed, 0 failed, 7 existing skips |
| Separate CLI lane | 196 passed, 0 failed, 0 skipped |
| Targeted production analyzer verification | Passed |
| Diff whitespace/error check | Passed |

The 28 new cases cover clear/shutdown during active HTTP sends, existing and newly arriving waiters during retirement, lookup-before-wait reservations, partial pair rollback, canceled waiters, eventual disposal, idle reclamation, invalid limits, distinct-profile aggregate authority, upward limit growth, and repeated concurrent clear/sweep/shutdown. The stress cases admit 200 operations each while cleanup runs; they check completed counts and absence of overlapping owners at limit one, without assuming waiter FIFO order.

The full suite's seven skips were the existing sample plugin smoke, three Apple packaging-availability cases, and three Windows symlink durability cases. None was introduced or changed here. The CLI build's generated optional-dependency lockfile additions were removed; no dependency delta is included.

Evidence directory: `artifacts/gate-lifecycle/` in the isolated worktree. Files include `red/gate-lifecycle-red.trx`, `green/gate-lifecycle-final-focused.trx`, `full/gate-lifecycle-full.trx`, `cli/gate-lifecycle-cli.trx`, ten `repeated/gate-repeat-*.trx` receipts, and `independent-review.md`.

## Review and limits

A separate read-only Codex process returned **APPROVE** for the changed production code and the initial lifecycle tests. It did not execute tests or claim consumer acceptance. The parent then added five supplemental tests covering the review's distinct-profile, limit-growth and concurrent-cleanup coverage gaps; production code remained unchanged after review.

The parent confirmed the actual Common project targets net8.0. The reviewer had not inspected framework metadata and appropriately flagged the target requirement of `Stopwatch.GetElapsedTime` for verification.

Exact nonzero idle-age reset and invocation of an old queued callback after rearming remain source-reviewed rather than directly tested with an injected clock/timer. No performance benchmark, live service authentication, completed download/import, private SDK runtime, long-duration soak, or five-plugin runtime proof is claimed for this candidate. Noncooperative callbacks retain ownership until they actually finish. Historical branch recovery and atomic download-state snapshots are separate work.
