# Queue snapshot consistency and safe legacy retention — 2026-09-06

## Integration boundary

Candidate branch: `fix/queue-snapshot-consistency-20260906`.
Base: Gitea Common main `57fd6fe3300be38c86e97e84abf82c477b7c718b`, which includes the gate-lifecycle and HTTP-helper ownership changes.
Worktree: `C:/Users/Alexandre/.devspace/worktrees/common-361d4696`.

The user is integrating the preceding candidates. This pass changes only Common in an isolated worktree. It does not merge main/master, modify consumer pins, update the user's other branches, or delete worktrees. Publishing this candidate is not ecosystem acceptance. Independent review, required remote CI, and downstream adoption remain separate gates.

## Observed defects

The initial test-only run compiled successfully and produced five failed cases plus one passing compatibility characterization:

1. DTO capture combined revision, attempt state and timestamp from different `ApplyTransition` operations. For example, an observed revision/state tuple did not correspond to either complete publication.
2. The mutation key combined an attempt GUID and revision from different internal restore publications; the stress witness also observed a GUID assembled from parts of two different values. This is an internal-publisher stress test, not evidence that normal startup restoration routinely overlaps published items.
3. Legacy queue polling threw `InvalidOperationException: Nullable object must have a value` when `CompletedAt` changed between the separate `HasValue` and `Value` reads.
4. A retention sweep removed a newly installed active replacement after inspecting the expired object previously stored under the same download ID. The ordinary subclass witness observed 15 lost replacements.
5. The same replacement-loss witness failed with a subclass defining value equality; it also observed 15 losses. A value-comparing conditional dictionary removal is not an adequate object-identity guard.

The concurrent witnesses have exact state oracles and bounded iteration counts, but do not promise a particular scheduler interleaving. Dedicated reader/writer threads start together, an observation must occur before writes proceed, and both are joined. The observed red results are not inferred from a source inspection.

## Shared repair

All production changes are in `src/HostBridge/HostBridgeDownloadTracker.cs`.

### One base-state observation boundary

A dedicated internal per-item `SnapshotSync` coordinates every mutable base-field writer with `HostBridgeDownloadItemDto.FromItem`, attempt initialization/restoration/transition publication, and mutation-key capture. `StartedAt` preserves its full DateTime value and Kind; `TotalSize` has atomic scalar reads; existing status/progress/completion scalar reads remain atomic. GUID reads and identity/revision capture use the same synchronization boundary as their writers.

The new lock is deliberately distinct from `MutationSync`. A persistence operation can already hold one item's mutation lock while serializing other items; acquiring their mutation locks during capture could produce a cross-store cycle. The snapshot lock is a leaf: its critical sections neither await nor invoke user callbacks, acquire store membership/persistence/mutation locks, or acquire another item's snapshot lock.

The established AttemptV2 order is extended to membership -> item mutation -> persistence -> item snapshot. Initialization uses its existing initialization lock -> snapshot. Individual captures release the snapshot lock before moving to another item. Legacy retention uses membership -> snapshot for its bounded eligibility/removal operation.

### Exact-instance legacy eviction

Every live legacy membership write now participates in the existing membership lock. The production `CollectLegacyItem` helper rechecks that the observed candidate is the same object currently registered for that key, then captures terminal status and one nullable completion value under the item's snapshot lock before removing it.

A replacement is retained even when a plugin subclass considers it value-equal to the old item. A removed, reactivated, undated or freshly completed item is handled using its current state. The `evicted` flag reflects actual removal. Disk I/O and warning notification remain outside the newly added legacy membership critical sections. Constructor-only population remains before store publication.

The helper rejects AttemptV2 stores; V2 high-water retention and durable removal policy are unchanged. Legacy expiry remains strictly greater than the configured retention interval, not greater-than-or-equal. The retention change removes queue entries only; filesystem deletion logic is not changed.

## Preserved contracts and limitations

- No public signatures, dependencies, JSON field names, schema versions, settings, retry policies, retention thresholds or warning budgets are changed.
- `GetSnapshot()` still returns live item references. It is not an immutable view and does not promise a globally simultaneous membership snapshot.
- `FromItem()` captures one consistent base-state observation into a detached, still-mutable DTO. The DTO itself is not an immutable type.
- Separate calls such as `SetStatus`, `SetProgress` and `CompletedAt = ...` remain separate mutations. This change does not turn a plugin's multi-call operation into a transaction, enforce terminal-state policy, or make arbitrary UI property reads atomic.
- Provider-only subclass fields, including Qobuz task/cancellation handles and messages and Tidal UX-only fields, are outside the base DTO and were not moved or changed.
- The added lock is allocated once per item, not per progress update. Captures and eviction checks use bounded critical sections. No benchmark or throughput improvement is claimed; contention and added synchronization cost have not been benchmarked.
- Existing legacy filesystem-delete/path-ownership behavior and cross-store authority semantics are not certified by this slice.

## Completed validation

| Validation | Passed | Failed | Existing skips |
| --- | ---: | ---: | ---: |
| Initial regression run before production changes | 1 | 5 | 0 |
| Initial affected identity/CAS/hardening run after repair | 58 | 0 | 0 |
| Full HostBridge-focused suite | 417 | 0 | 0 |
| Final full Common default suite | 7,590 | 0 | 7 |
| Separate CLI lane | 196 | 0 | 0 |
| New regression and boundary cases | 22 | 0 | 0 |

The 22 new cases passed ten consecutive repetitions, totaling 220 successful case executions. These repetitions are additional runs, not additional distinct tests.

Supplemental deterministic checks cover deferred candidate replacement (including value equality), prior removal, reactivation, cleared/refreshed completion timestamps, the exact one-tick expiry boundary, all terminal/nonterminal statuses, rejection of legacy eviction on V2, LegacyV1 and AttemptV2 persistence round trips, and preservation of live membership versus detached DTO behavior. A lock-separation test models capture while different item mutation locks are held; it is not a full multi-store soak test.

Accepted receipts live under `artifacts/queue-snapshots/`:
- `red/queue-snapshot-red.trx`
- `green/queue-snapshot-initial-green.trx`
- `green/queue-snapshot-hostbridge.trx`
- `full/queue-snapshot-full.trx`
- `cli/queue-snapshot-cli.trx`
- `repeated/queue-snapshot-repeat-1.trx` through `queue-snapshot-repeat-10.trx`

Targeted production analyzer verification and diff checks passed. The optional CLI build's generated 26-line lockfile delta was removed; no package change is included. No new tests were skipped, and the seven full-suite skips remain explicitly unexecuted coverage.

## Review status

Two read-only external reviewer invocations were blocked before execution by the tool layer. There is no independent APPROVE verdict for this candidate. Do not reinterpret successful local tests as independent review.

The author-side lock-order and ownership analysis is recorded at `artifacts/queue-snapshots/author-review.md`. It is author review only. The external request is retained at `artifacts/queue-snapshots/review-request.md` for a later independent pass.

## Consumer impact survey

The inspected plugin sources confirm the shared tracker on all four streaming download clients:
- Apple uses `HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>.ForPlugin` and passes `GetSnapshot()` into its host-item projection.
- Tidal uses the same store with `TidalDownloadItem : HostBridgeDownloadItem` and an existing custom restore factory.
- Amazon uses the base item/store and the same polling pattern.
- Qobuz uses `HostBridgeDownloadTrackerStore<QobuzDownloadItem>`; `QobuzDownloadItem` derives from the Common base and restores through `FromHostBridgeDto`.

The searched Brainarr production roots did not contain this tracker type; Brainarr remains import-list-only. The repair therefore belongs in Common rather than four per-provider forks. Apple/Tidal tracking refs were freshly fetched; Qobuz/Brainarr searches used their available Gitea tracking snapshots after the batch refresh was blocked. Amazon's configured fetch is single-branch, so its available main tracking ref is not represented as a freshly verified remote tip. No claim is made that every consumer currently contains the candidate.

## Remaining acceptance gates

Independent review and exact-head remote CI must precede promotion. Consumer pin/sentinel updates, parity/build/package validation and fresh Docker coexistence have not been run for this candidate. Earlier package or runtime receipts are not reused as evidence. No live authentication, downloads/imports, private SDK runtime or long-duration soak was exercised.

## Framework reference checked

The .NET 8 ConcurrentDictionary implementation compares a candidate value using `EqualityComparer<TValue>.Default` when conditional removal requests value matching. It does not guarantee reference identity for a subclass overriding equality. Its enumerator is concurrent-safe but not a moment-in-time snapshot. The implementation was consulted before choosing the exact-reference guard:

`https://raw.githubusercontent.com/dotnet/runtime/v8.0.0/src/libraries/System.Collections.Concurrent/src/System/Collections/Concurrent/ConcurrentDictionary.cs`
