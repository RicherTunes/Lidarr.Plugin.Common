# Task 5A Report: secure-removal neutralization and durable WAL

## Outcome

Staged the authorized revert of `947b758` while retaining `c1763bd`, then removed the restored
`OwnedStagingTree` pathname-recursive deletion helper in the same change. AttemptV2
`deleteData:true` now completes bounded cancellation and returns `QUEUE_REMOVAL_DEFERRED` with
the queue mapping and filesystem evidence retained. `deleteData:false` remains exact queue-only
removal, and LegacyV1 behavior is unchanged.

Added the frozen typed removal contracts, bounded relative-path type, root-scoped OS writer
lease, and schema-v1 `FileRemovalJournal`. The journal enforces the four legal state/disposition
edges, strict canonical JSON and bounds, full-record CAS, unique durable temps, atomic replace,
parent-directory flush where supported, compaction barriers, link rejection, and whole-scan
fail-closed behavior.

## TDD evidence

Initial RED:

```powershell
dotnet test tests/Lidarr.Plugin.Common.Tests.csproj -c Release --no-restore -m:1 --filter "FullyQualifiedName~RemovalNeutralizationTests|FullyQualifiedName~RemovalJournalDurabilityTests"
```

Observed exit `1` with expected `CS0246` failures for the missing frozen 5A contracts including
`RemovalJournalState`, `SafeOwnedRoot`, `RemovalWriterLease`, and `FileRemovalJournal`.

Architect-review RED added regressions for concurrent same-token CAS, null nested identity, and
post-unlink compaction failure. The focused run observed two concrete failures: a
`NullReferenceException` during strict scan and an absent record after the
`BeforeCompactionParentFlush` fault. The concurrency test was corrected to enter from two
threads before its GREEN result was accepted.

Focused GREEN:

```powershell
dotnet test tests/Lidarr.Plugin.Common.Tests.csproj -c Release --no-restore -m:1 --filter "FullyQualifiedName~RemovalNeutralizationTests|FullyQualifiedName~RemovalJournalDurabilityTests|FullyQualifiedName~HostBridgeQueueRemovalV2Tests"
```

Result: **52 passed, 0 failed, 11 explicitly skipped, 63 total**. The skips are obsolete
pre-coordinator physical-deletion expectations; 5A intentionally defers all such operations
until 5B.

## Verification

- Pre-change canonical baseline: **7,183 passed, 0 failed, 3 skipped, 7,186 total**.
- Warnings-as-errors solution build: **0 warnings, 0 errors**.
- `rg -n "QuarantineAndDelete|DllImport|LibraryImport" ...`: exit `1` (no matches).
- `git diff --check`: clean.
- Pre-review canonical run: **7,203 passed, 0 failed, 12 skipped, 7,215 total**.
- Final post-review canonical run: **7,205 passed, 0 failed, 13 skipped, 7,218 total**;
  build **0 warnings, 0 errors**, exit `0`.

## Review fixes

- Serialized every journal operation with one per-instance gate so same-process CAS cannot
  accept the same expected token twice.
- Revalidated the OS writer lease for every operation and fail closed with `SecondWriter` after
  lease loss.
- Rejected lock-file, journal-directory, and record-path links, including dangling links.
- Hardened nested identity validation so null/invalid fields return `Corrupt` rather than throw.
- Restored the exact completed WAL record if a compaction fault occurs after unlink but before
  the parent durability barrier.
- Propagated supported Unix parent-directory flush IO/ACL failures as durability failures.

## Architect verification

Independent final verdict: **APPROVE**, with no remaining blocking findings.

## Commit

Implementation, revert, tests, and this report are committed together as
`fix(queue): neutralize deletion and add durable removal WAL`.
