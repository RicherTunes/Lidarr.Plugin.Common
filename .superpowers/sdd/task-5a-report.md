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

---

## Independent-review remediation

An independent review of `6a7155d` returned `CHANGES_REQUIRED`. The remediation is a separate
commit and preserves that accepted baseline.

### Review findings and root causes

1. The public legacy `Remove(..., deleteData:true)` path still bypassed the AttemptV2 deferred
   transaction. It now fails closed without removing mapping or files; `deleteData:false`
   remains exact queue-only, and LegacyV1 is unchanged.
2. A writer lease could be reused by two live journal objects. The lease now atomically binds
   one owner, revalidates the held OS lock/root, and unbinds only when that journal is disposed.
3. Root identity was a path hash rather than durable identity. `SafeOwnedRoot` now creates or
   strictly reads a CSPRNG 256-bit `.lpc-root-id`, uses Unix `0600` or a protected owner-only
   Windows `FullControl` ACL, flushes and reads back exactly, and holds a read handle without
   delete sharing for its lifetime. Every operation revalidates root/ancestor links, marker
   bytes/permissions, state/journal/lock control paths, and lease ownership. Unix open handles
   cannot prevent same-identity rename; path and marker revalidation closes accidental or
   different-identity drift, while malicious same-identity mutation remains the documented
   non-goal.
4. WAL parsing validated only the top-level shape. Nested `queueKey` and `fileIdentity` now
   require exact properties and values; accepted bytes must equal canonical serialization.
   Tokens hash those canonical bytes, so whitespace, reordering, and noncanonical GUID casing
   fail closed.
5. Compaction unlinked the only durable mapping and attempted best-effort restoration after a
   fault. It now atomically renames the complete completed record to recognized
   `<operation-id>.compacted`, flushes the parent, and leaves that mapping readable/scannable.
   All `.json`, `.compacted`, and unknown entries count toward the 10,000-record bound; unknown
   entries block creation and recovery fail closed.
6. The frozen result code symbol is now exactly `QueueDurabilityFailure` with value
   `QUEUE_DURABILITY_FAILURE`.

### Remediation RED evidence

- Legacy/lease/nested/canonical/code filter: **22 total, 10 passed, 12 expected failures**.
  Failures covered the AttemptV2 legacy delete bypass, second live journal, missing frozen
  symbol, invalid queue keys, and noncanonical encodings.
- Root/compaction filter: **3 expected failures, 1 capability skip**. No durable root marker,
  marker replacement was not detected, and successful compaction did not leave a recognized
  durable compacted record.
- Permission-drift regression failed because journal scan still succeeded after ACL/mode drift.
- Final architect RED: compacted-bound creation succeeded with 10,000 compacted mappings;
  control-link creation was unavailable on this Windows host. Exact ACL rights and per-operation
  control-path revalidation were added and independently re-reviewed.

### Remediation GREEN evidence

Focused owning command:

```powershell
dotnet test tests/Lidarr.Plugin.Common.Tests.csproj -c Release --no-restore -m:1 --filter "FullyQualifiedName~RemovalNeutralizationTests|FullyQualifiedName~RemovalJournalDurabilityTests|FullyQualifiedName~HostBridgeQueueRemovalV2Tests|FullyQualifiedName~HostBridgeDownloadTrackerCleanupRaceTests|FullyQualifiedName~HostBridgeDownloadTrackerHardeningTests"
```

Result: **93 passed, 0 failed, 14 explicitly skipped, 107 total**. Skips are the accepted 5B
physical-deletion expectations and honest Windows link-capability skips.

Warnings-as-errors solution build: **0 warnings, 0 errors**. Forbidden native/deletion symbol
scan returned no matches; `git diff --check` passed.

Final canonical command:

```powershell
pwsh scripts/test.ps1 -TestProject tests/Lidarr.Plugin.Common.Tests.csproj -Configuration Release
```

Result: **7,234 passed, 0 failed, 15 skipped, 7,249 total**; build **0 warnings, 0 errors**;
exit `0`.

Independent architect final verdict after the compacted-bound, exact-ACL, and control-path
fixes: **APPROVE**, with no remaining security, concurrency, cleanup, CAS, schema, or
crash-compaction blocker.
