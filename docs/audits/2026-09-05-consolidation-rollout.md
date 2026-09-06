# Consolidation rollout verification — 2026-09-05

This follow-up supersedes the rollout status in the initial `2026-09-05-gitea-consolidation.md` checkpoint. The original report remains historical evidence, not the current acceptance decision.

## Canonical source and safe integration

**Rollout complete: Common and all five consumers are merged into their Gitea main branches.** This closes this audited integration wave, not every historical branch or remaining diagnostic listed below.

Common's audited tip `e22be313832bf73673279db24a5d47af742f346d` and re-authored Gitea landing tip `c08799b478945ca8aad0a1060d297e021331cc94` have exactly the same Git tree: `296cfa8a9f98bfd9f760115078bc8be3524f1e55`. The canonical landing is PR #121, not the duplicate AGit requests. All five consumer gitlinks, sentinel files and newest changelog pin entries use `c08799b478945ca8aad0a1060d297e021331cc94`, with Common version `1.18.0`.

The following merges were confirmed by Gitea's API after successful checks on the exact expected PR head. The server's normal merge operation was used; no force merge, branch-protection relaxation, status fabrication or source-branch deletion was requested.

| Repository | Canonical PR | Integration state | Confirmed merge commit |
| --- | --- | --- | --- |
| Lidarr.Plugin.Common | 121 | Merged; all six PR checks passed | `845d029402c2451e7df628e3a8b2da65437636d1` |
| AppleMusicarr | 70 | Merged; all five PR checks passed | `34fd7888510cc19d8fac8baeb4cfdc008bbb9645` |
| Qobuzarr | 105 | Merged; all four PR checks passed | `64cd0253aa0abe21e2e0a725984f760c377f0182` |
| Tidalarr | 77 | Merged; all four PR checks passed | `030d788a0d33f06965d6895efbfe64f9b1dab650` |
| Brainarr | 77 | Merged; all four PR checks passed | `754543790b4a110739421f7cc49d42de605aa0d6` |
| AmazonMusicarr | 66 | Merged; all four PR checks passed | `4a0db1f60ffe4a4039e79f4aed0355911f85c95c` |

Post-merge push workflows are separate executions; successful PR checks must not be relabeled as completed post-merge checks. Common's post-merge run `13073` was separately inspected and completed successfully on `845d029`, including all six jobs and the live ecosystem-contract job.

Duplicate requests Common #122/#123, Apple #71, Brain #78 and Tidal #78 were closed only after identifying their canonical counterparts and, where applicable, confirming exact source-tree identity. Original audit branches and all pre-existing branches were preserved. Concurrent re-authoring was reconciled through PR comments and tree comparisons, rather than force-updating another branch.

## Findings addressed

### Shared HTTP retry behavior

`CachingHttpExecutor` now uses the canonical `RateLimitHeaderUtilities.ResolveRetryAfter`; an absent header still falls back to the configured policy. `GenericResilienceExecutor` clamps negative delays and compares them with the remaining budget without overflowing `DateTime + untrusted duration`. Expired-budget rejection returns the original usable response without disposing it.

The pre-fix run produced two genuine regression failures; the affected HTTP/resilience run subsequently passed 83 tests. The full Common default suite passed 7,396 tests, with seven existing skips, and the separate CLI lane passed 196 tests. Independent source reviews approved the bounded changes. Common's canonical Gitea run 13054 passed the full build/test and CLI lanes, template, dependency scan, lint and secret scan.

### One shared smoke runner with safe ownership

The optional local-CI smoke stage delegates to the shared multi-plugin runner instead of owning another mount/authentication/schema implementation. Explicit contracts cover all five plugins, and unknown or duplicate plugin names fail closed. Container creation records an immutable ID using a cidfile; deletion requires that ID and the matching per-run ownership label. Existing named containers are not replaced. Ports are loopback-only and ephemeral by default. Persistent-state access is leased and rechecked before mutation; disposable state is cleaned, while diagnostic reports are retained separately. Early import errors are no longer masked by the former cleanup trap.

Independent adversarial reviews caught warning-contaminated container-ID capture, disposable-state accumulation, a lease/name-check race and report deletion; these were corrected before acceptance. Shared smoke safety regression coverage contains 29 passing assertions.

### Package identity and compiled host requirements

Version resolution uses MSBuild evaluation with the packaging configuration and flags rather than trusting XML fallback literals. The canonical package path validates ZIP filename, manifest and package metadata together; an unknown version cannot quietly become a fabricated package version. Orphaned analyzer satellite DLLs are removed.

The host requirement guard reads assembly metadata without loading plugin code and refuses an understated minimum host. All five old ZIPs were rejected before the consumer declaration fixes. Four consumers compile against Lidarr `3.1.2.4913`; Tidal compiles against `3.1.3.4970`. Their manifests now declare those respective minimums, including Apple's project-level minimum and supplementary manifests. The shared runtime gate also checks the actually running host.

The original Qobuz package-name mismatch is corrected: `qobuzarr-0.5.12-net8.0.zip` now agrees with its manifest and metadata. This was fixed through Common adoption, not another Qobuz-specific packaging implementation.

### Enforced warning budgets and dependency alignment

Every consumer enables the shared `UniqueDiagnostics` metric and enforcement without raising its previous numeric budget. Repeated diagnostics are deduplicated across builds; unrepresented summary warnings are conservatively charged. The legacy occurrence metric remains available for existing callers. The unique metric is not directly comparable to the old repeated-warning count, and it does not mean all remaining warnings are fixed.

The adopted Common dependency floor and regenerated affected lockfiles align `System.Security.Cryptography.Xml` at `8.0.4`. Dependency scans on the canonical PRs remain independent merge gates. A restore warning is not, on its own, proof that a vulnerable DLL shipped; package closure and actual artifact identity were checked separately.

### Consumer consistency and test isolation

Brain's pricing-history tests now use the already-existing exclusive collection for process-wide mutable history. A reflection contract guards the required collection configuration. Production pricing behavior and test assertions were not weakened.

Host-floor adoption exposed real documentation failures: Brain's README consistency test, then its broader setup-guide/wiki consistency gate, and Qobuz's newest Common pin changelog test. Current documents and examples were corrected while historical release requirements remained historical. Brain's complete shared lint runner passed locally after the six-file docs-only correction `bd34071298ccb14bd8217dd33bd2cf6e350d8654`. Its final remote run `13074` then passed all four required checks before the normal protected merge; local evidence was not substituted for remote checks.

## Local verification matrix

These are completed runs, not selected-test estimates. Each consumer's canonical local verification included its configured production build, ILRepack package, package identity/compiled-host gate, packaging closure and all declared deterministic test projects.

| Consumer | Test projects | Passed | Failed | Existing skips | Enforced unique warnings / budget |
| --- | ---: | ---: | ---: | ---: | ---: |
| AppleMusicarr | 4 | 2,268 | 0 | 0 | 29 / 200 |
| Brainarr | 3 | 3,405 | 0 | 5 | 62 / 80 |
| Qobuzarr | 4 | 3,770 | 0 | 7 | 770 / 1,000 |
| Tidalarr | 2 | 1,836 | 0 | 16 | 16 / 100 |
| AmazonMusicarr | 3 | 1,503 | 0 | 7 | 16 / 100 |
| Total | 16 | 12,782 | 0 | 35 | Per-consumer gates above |

Receipts are under `artifacts/audit-20260905/canonical-<consumer>.stdout.log` and the corresponding stderr logs in the Common audit workspace. Common's seven skips and the consumers' 35 skips were not converted to passes; no new exclusions were introduced to make this rollout green. Apple local/runtime proof uses the existing SDK-disabled public packaging mode. Gitea's `sdk-compile` success establishes compilation, not licensed/private SDK runtime correctness.

## Actual five-plugin coexistence proof

`artifacts/audit-20260905/canonical-five.stdout.log` records each tested plugin HEAD, canonical Common gitlink, version, minimum host and ZIP SHA-256 before calling the shared runner. Package provenance had to match its committed audit HEAD. The canonical PR trees were separately compared with these audit trees; re-authored SHAs are not misrepresented as the original package's build SHA.

The real host was Lidarr `3.1.3.4970`, image digest:

```text
sha256:b9456f23d8aea47f86d36482a71ad217dd3718ab98955cc23d9864be60f7a411
```

All packages were loaded simultaneously. The runtime exposed all nine required provider implementations:

- Qobuz: `QobuzIndexer`, `QobuzDownloadClient`.
- Tidal: `TidalLidarrIndexer`, `TidalLidarrDownloadClient`.
- Apple: `AppleMusicLidarrIndexer`, `AppleMusicLidarrDownloadClient`.
- Amazon: `AmazonmusicLidarrIndexer`, `AmazonmusicLidarrDownloadClient`.
- Brain: `Brainarr` import list.

The proof ended with `CANONICAL FIVE-PLUGIN COEXISTENCE PASS`; its run-owned container was removed. The obsolete earlier diagnostic container was also removed only after its immutable ID and recorded run label matched; its mounted evidence was preserved. The existing Gitea runner and other workloads were not removed.

## Boundaries and remaining work

No live subscription authentication, protected-content download, completed-download import, private Apple SDK runtime or long-duration soak was claimed. The smoke proof verifies packaging, host compatibility and simultaneous provider discovery, not every live service behavior.

Original checkouts under `D:/Alex/github` retain their pre-existing dirty files and divergent histories. Remote Gitea main integration must not be confused with resetting those local mains. The 160 historical local branches still require selective patch-identity and behavior review; their count does not establish 160 distinct missing features. No historical branch was deleted or merged wholesale as part of this rollout.

Remaining diagnostic debt is bounded by real enforced budgets, not eliminated. Follow-up reductions should fix warning causes and add behavioral tests where appropriate rather than increase budgets or suppress diagnostics. Public external DRM/host seams remain plugin-owned where assembly identity requires it; this rollout does not force legitimately different adapters into a single inappropriate abstraction.
