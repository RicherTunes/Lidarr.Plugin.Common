# Task 5 Report: Two-phase removal and owned-path containment

## Outcome

Implemented AttemptV2 `RemoveAttemptAsync` as a bounded two-phase transaction while leaving
legacy `Remove` behavior unchanged. Added strict owned-staging validation that rejects root
equality, lexical escapes, root/target/nested reparse points, and deletion failures without
removing the queue record or filesystem evidence.

## TDD evidence

RED command:

```powershell
dotnet test tests/Lidarr.Plugin.Common.Tests.csproj -c Release --filter FullyQualifiedName~HostBridgeQueueRemovalV2Tests
```

Observed before production edits: build failed with `CS1061` at every new call site because
`HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>` did not contain a definition for
`RemoveAttemptAsync`. This was the expected missing-feature failure.

A second focused RED cycle pinned immutable root configuration. Before snapshotting the root,
`ConfiguredOwnedRoot_IsSnapshottedWhenStoreIsConstructed` failed with expected
`QUEUE_REMOVED` versus actual `QUEUE_SAFE_ORPHAN_OUTSIDE_ROOT`.

Architect rejection triggered a third narrow RED cycle. The uncooperative-worker test failed
with an external `TimeoutException` because merely cancelling the supplied token did not bound
a worker that ignored it. The LegacyV1 observer characterization failed because no exception
was propagated. Two Unix/capability-gated tests also pin dangling root and target symlink
identity even when both `Directory.Exists` and `File.Exists` return false.

GREEN focused acceptance:

```powershell
dotnet test tests/Lidarr.Plugin.Common.Tests.csproj -c Release --no-restore -m:1 --filter "FullyQualifiedName~HostBridgeQueueRemovalV2Tests|FullyQualifiedName~HostBridgeDownloadTrackerCleanupRaceTests|FullyQualifiedName~SimpleDownloadOrchestratorPathContainmentTests"
```

Result: **Passed: 31, Failed: 0, Skipped: 0**.

The two existing AttemptV2 pre-admission observer-reporting regressions found by the canonical
runner were then included with focused acceptance. Result: **Passed: 33, Failed: 0, Skipped: 0**.

The serial `-m:1` setting was used after parallel verification encountered the repository's
known transient `CS2012`/locked build-output condition.

## Additional verification

```powershell
dotnet format src/Lidarr.Plugin.Common.csproj analyzers --verify-no-changes --include src/HostBridge/OwnedStagingTree.cs src/HostBridge/HostBridgeDownloadTracker.cs tests/HostBridge/HostBridgeQueueRemovalV2Tests.cs
```

Result: exit code 0.

```powershell
dotnet build lidarr.plugin.common.sln -c Release
```

Result: build succeeded with 0 errors. MSBuild emitted one transient `MSB3026` retry warning
for a locked Abstractions output and recovered automatically.

Canonical command:

```powershell
pwsh scripts/test.ps1
```

The first run built with 0 warnings and 0 errors and ran 7,176 tests: 7,173 passed, 2 failed,
1 skipped. Both failures were existing AttemptV2 pre-admission observer-reporting tests and
identified that observer containment had been applied too broadly. Containment was narrowed
to the committed removal operation and the shared notifier was restored. The canonical rerun
built cleanly and ran 7,176 tests: 7,174 passed, 1 failed, 1 skipped. The sole failure was
`ConcurrentAcceptedAdmissions_AlwaysCommitBeforeRegistrationAndWork`, whose work observer
reported transient Windows persistence-file sharing errors during its concurrency probe. An
immediate isolated rerun passed: **Passed: 1, Failed: 0, Skipped: 0**. No MultiPlugin ALC drift
appeared in the canonical fast lane.

A separate unfiltered test-assembly run earlier surfaced environment/ecosystem fixture drift
in `MultiPluginAlcTests.LoadAllPluginsInIsolatedAlcs_NoAssemblyVersionDrift` for brainarr,
qobuzarr, and tidalarr versus the freshly built Common DLL; that category is excluded from the
canonical fast lane.

## Coverage and self-review

- Active removal persists `Cancelling` before awaiting the worker, then transitions to
  `Cancelled` and removes by exact attempt/revision.
- Timeout is enforced externally with `Task.WaitAsync`, so even a worker that ignores its
  cancellation token is bounded. Timeout, worker exception, and independent worker
  cancellation preserve the cancelling record and filesystem evidence.
- Terminal attempts bypass the worker; stale CAS never stops a worker or removes evidence.
- Root equality, sibling-prefix escape, `..` escape, root/target/nested reparse points, and
  root filesystem replacement are refused with stable safe-orphan codes.
- Nested-link refusal is validated before deletion; both local partial data and the external
  link target remain present.
- Dangling Unix root and target symlinks are detected through link identity rather than
  existence predicates and return the stable link-traversal refusal code.
- Missing in-root targets are successful no-ops.
- Another active attempt at the same OS-canonical path retains the shared tree while the
  terminal record is removed. Membership locking prevents a new owner from racing between
  the ownership check and deletion.
- The configured owned root is canonicalized/snapshotted at store construction and is not
  affected by later mutation of the options reference.
- Removal persistence warning observers execute after locks; observer exceptions are
  contained only after removal is committed. LegacyV1 propagation and existing AttemptV2
  pre-admission fault reporting remain unchanged. The removal test asserts its throwing
  observer was actually invoked exactly once.
- Path decision results contain stable codes only and never expose absolute paths.
- `git diff --cached --check` passed before commit.

## Commit

Implementation, tests, and this report: `HEAD feat(queue): make removal a bounded two-phase transaction`
