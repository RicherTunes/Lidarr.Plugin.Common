# Gitea consolidation audit — 2026-09-05

**Review-branch checkpoint / NOT merged / NO plugin repins / NOT full ecosystem acceptance.**

This records parent-provided audit facts plus independent read-only inspection of the four-file authfail candidate and selected saved receipts. The recorder ran no tests/builds, Docker, live APIs, or delegation, and changed only this report and `artifacts/audit-20260905/smoke-review.md`. Historical execution results below are not fresh reviewer runs.

## Preservation and inventory

- All original dirty checkouts and branches were preserved: no branch pruning, force push, stash, or reset. Gitea was fetched without recursive submodule updates.
- Requested Windows junction `D:/Alex/ChatGPT_Projects/Alex/github` resolves to `D:/Alex/github`. Original histories diverged; path aliases must not be mistaken for independent copies.
- `D:/Alex/ChatGPT_Projects/lidarr-audit-20260905` contains Common and Qobuz isolated worktrees plus an Amazon fresh clone ONLY. No Apple, Brain, or Tidal worktrees were created for this audit.
- Original local branch counts: Common 40, Apple 36, Brain 17, Qobuz 28, Tidal 39 = 160 preserved. Names and divergent histories do not establish 160 unique unmerged features.
- Brain local `main` already matches Gitea; its checked-out `tmp/gh-contrib-refresh` is old.
- Updater is a separate Python repository with Gitea origin and 66 incoming commits; its dirty checkout was preserved. It was not audited as a sixth C# plugin.

Gitea main snapshot supplied by parent:

| Repository | Main SHA |
| --- | --- |
| Common | `02648bc1994cc7205e1be328306cee44f5182182` |
| Apple | `096b16928b96d919a197326c701aa2e329edc397` |
| Brain | `8dea5f5ff463691f81a40faeffc1a295572fe5c8` |
| Qobuz | `01531f83b81729e165f6c4163882ab6d6037a5ba` |
| Tidal | `86e93b23ec10afb4773e4fe7f6c79ed6a1b499d5` |
| Amazon | `30f7824d5df5ec143c5b09efe67ee1d3b911dc1d` |

All five plugin gitlinks AND `ext-common-sha.txt` pin `b4b31455e2773ff3c845be81a0e78b8157f54f0c`, 20 commits behind this Common main. Missing changes include AttemptV2 removal journal/queue work, package version/satellite fixes, and the crypto-XML floor. All five latest `.gitmodules` already use Gitea. The initial GitHub missing-commit recursive-fetch failure came from the stale LOCAL Apple checkout, not current Gitea main.

## Completed candidate fixes and independent review

- HTTP commit `f634533b1faab18751566ad5566de206cbf3f2e3`: `src/Services/Http/CachingHttpExecutor.cs` reuses canonical `RateLimitHeaderUtilities.ResolveRetryAfter` while retaining null fallback to policy backoff. `src/Utilities/GenericResilienceExecutor.cs` clamps negative delays and compares delay with remaining budget, avoiding `DateTime + untrustedDelay` overflow.
- Regression sources: `tests/GenericResilienceExecutorTests.cs`, `tests/Services/Http/CachingHttpExecutorTests.cs`. Parent reports TDD 2 red / 2 green before the change and 83 affected tests green afterward. Independent narrow HTTP review: `artifacts/audit-20260905/review.md` (unchanged); its nonblocking coverage gaps and unrelated extreme-policy arithmetic debt remain applicable.
- Authfail candidate: exactly two `$mode:` expressions became `${mode}:` in `scripts/lib/e2e-authfail.psm1:537,543`. `scripts/tests/Test-E2EAuthfailImport.ps1` imports the real module without live calls, fails closed for imports/required exports/modes, and runs as a dedicated PowerShell step in both CI files. Verdict: **APPROVE**, narrow scope, in `artifacts/audit-20260905/smoke-review.md`.
- Prior smoke failed during module import at those two lines; the later Cleanup trap masked that primary error. Parent independently reran 3 authfail + 14 WorkflowContentsValid + 68 EcosystemCiContract assertions green and reports real Docker smoke passed after repair. Red/green receipts: `artifacts/audit-20260905/authfail/`. No final smoke-patch commit/push state is claimed.
- No CLI-generated lockfile delta is included: only our own generated `src/packages.lock.json` change was restored.

## Unit and analyzer evidence

Paths below are relative to Common root, unless prefixed `../qobuzarr`.

| Lane | Result | Receipt |
| --- | --- | --- |
| HTTP red | 2 failing regressions (parent TDD evidence) | `artifacts/audit-20260905/red/retry-budget-red.trx` |
| HTTP affected green | 83 pass, 0 fail | `artifacts/audit-20260905/green/common-http-green.trx` |
| Common default controlled | 7396 pass, 0 fail, 7 skip | `artifacts/audit-20260905/full-controlled/common-full-controlled.trx` |
| CLI flag lane | 196 pass, 0 fail, 0 skip | `artifacts/audit-20260905/cli/common-cli.trx` |

Saved green TRX counters were inspected by this recorder. Common's 7 skips comprise sample plugin smoke, 3 Windows symlink durability cases, and 3 Apple package closure cases; not all platform/package checks executed. Parent reports source-targeted `dotnet format` analyzer verification passed.

## Qobuz baseline and runtime evidence

- This is the OLD Common pin, with NO new HTTP code. `../qobuzarr/scripts/verify-local.ps1` passed Docker real-host extraction, build, ILRepack, packaging, closure, and four test projects: 2403 / 1019 / 194 / 154 passing = 3770 pass, 0 fail, 7 skip.
- Receipt: `../qobuzarr/artifacts/audit-20260905/baseline.stdout.log`; inspected totals agree. Warning budget: 2954 occurrences against 1000, NONBLOCKING. These are not 2954 unique defects.
- Confirmed package version mismatch despite green gate: `../qobuzarr/artifacts/packages/qobuzarr-0.1.0-dev-net8.0.zip` contains `plugin.json` version `0.5.12`. Common main already has conditional-version fixes `f32f981` / `a493e50`: adopt them, do not reimplement.
- Qobuz root `obj/project.assets.json` resolves `System.Security.Cryptography.Xml` 8.0.4; pinned Common `ext/Lidarr.Plugin.Common/src/obj/project.assets.json` resolves 8.0.3 and emits NU1903. This does not establish a vulnerable DLL shipped in Qobuz or exploitation.
- Parent reports actual schema smoke using repaired `scripts/multi-plugin-docker-smoke-test.ps1`, image `ghcr.io/hotio/lidarr:pr-plugins-3.1.2.4913`, digest `sha256:ae0b3b14769fdfeb73fe5d9e61ebcda04edf202244bcbd6323d2fe1381154f57`.
- Unique container `lidarr-audit-20260905-qobuz-baseline`, port 18769, exposed BOTH `QobuzIndexer` and `QobuzDownloadClient` schemas, then was cleaned. Pre-existing `gitea-act-runner-richertunes-codex` was untouched. Retained runtime files: `artifacts/audit-20260905/qobuz-baseline-smoke/lidarr-audit-20260905-qobuz-baseline/`.
- No live credential/download/import tests or five-plugin coexistence were demonstrated. Baseline runtime success does not validate new Common consumer adoption.

## Ranked open findings and adoption boundaries

1. Smoke lifecycle/ownership: `scripts/local-ci.ps1` optional smoke duplicates lifecycle, mounts the wrong `/plugins` path, makes unauthenticated calls, assumes importlist-only schemas, and uses a fixed name/port with destructive cleanup. Consolidate safely with tests into the existing runner, not another runner.
2. Existing runner completeness: `scripts/multi-plugin-docker-smoke-test.ps1` has role-aware expectations for Qobuz/Tidal/Brain only. Apple/Amazon are absent; unknown plugins skip expectations. Early import failures can be masked by its Cleanup trap; fixed-name ownership remains unresolved.
3. Consumer/package acceptance: old pins exclude fixes; green closure did not catch exact package version mismatch. Adopt Common fixes and verify both version identity and dependency closure.
4. Warning/dependency debt: establish a measured warning/dependency ratchet, separating repeated diagnostics from unique causes and resolved assets from shipped contents.
5. Branch recovery: establish patch identity and behavioral value before small tested merges; never wholesale-merge old mains on the strength of branch counts.

Shared adoption targets: Apple + Qobuz `CachingHttpExecutor`; Tidal + Amazon `ResolveRetryAfter`. Brain's host raw-header/LLM adapters are legitimately different. Keep host and public external DRM seams plugin-owned; do not churn typed DTO/raw JSON architectural differences.

Acceptance order: smoke role/ownership tests; fresh isolated five-consumer pin wave updating `ext-common-sha.txt` + gitlink together with parity/build/closure; five-package coexistence + exact version checks; warning/dependency ratchet; patch-identity/behavioral branch recovery through small tested merges.

## Exact reproduction commands (not run by recorder)

Run from `D:/Alex/ChatGPT_Projects/lidarr-audit-20260905/common`. These are reproduction recipes, not a claim to reproduce every historical environment flag. Commands that execute tests/Docker are for a subsequent authorized validation session. Preserve existing receipts by using a separate output directory.

```powershell
git diff -- .gitea/workflows/ci.yml .github/workflows/ci.yml scripts/lib/e2e-authfail.psm1
Get-Content scripts/tests/Test-E2EAuthfailImport.ps1
git show f634533b1faab18751566ad5566de206cbf3f2e3 -- src/Services/Http/CachingHttpExecutor.cs src/Utilities/GenericResilienceExecutor.cs
git rev-list --count b4b31455e2773ff3c845be81a0e78b8157f54f0c..02648bc1994cc7205e1be328306cee44f5182182
git -C ../qobuzarr show 01531f83b81729e165f6c4163882ab6d6037a5ba:.gitmodules
git -C ../qobuzarr show 01531f83b81729e165f6c4163882ab6d6037a5ba:ext-common-sha.txt
git -C ../qobuzarr ls-tree 01531f83b81729e165f6c4163882ab6d6037a5ba ext/Lidarr.Plugin.Common
pwsh -NoProfile -File scripts/tests/Test-E2EAuthfailImport.ps1
pwsh -NoProfile -File scripts/tests/Test-WorkflowContentsValid.ps1
pwsh -NoProfile -File scripts/tests/Test-EcosystemCiContract.ps1
dotnet test tests/Lidarr.Plugin.Common.Tests.csproj -c Release --filter 'FullyQualifiedName~GenericResilienceExecutorTests|FullyQualifiedName~CachingHttpExecutorTests' -p:EnableSourceLink=false -m:1 -p:UseSharedCompilation=false --results-directory artifacts/audit-20260905/reproduction/http --logger 'trx;LogFileName=common-http.trx'
dotnet test tests/Lidarr.Plugin.Common.Tests.csproj -c Release --filter 'State!=Quarantined' -p:EnableSourceLink=false -m:1 -p:UseSharedCompilation=false --results-directory artifacts/audit-20260905/reproduction/full --logger 'trx;LogFileName=common-full.trx'
dotnet test tests/Lidarr.Plugin.Common.Tests.csproj -c Release -p:IncludeCLIFramework=true --filter 'Category=CLI&State!=Quarantined' -p:EnableSourceLink=false -m:1 -p:UseSharedCompilation=false --results-directory artifacts/audit-20260905/reproduction/cli --logger 'trx;LogFileName=common-cli.trx'
dotnet format src/Lidarr.Plugin.Common.csproj analyzers --verify-no-changes --include src/Services/Http/CachingHttpExecutor.cs src/Utilities/GenericResilienceExecutor.cs
pwsh -NoProfile -File ../qobuzarr/scripts/verify-local.ps1
```

For a schema-only smoke rerun, first ensure this dedicated name/port is unused; the current runner still removes containers by name. Use a fresh staging directory and the existing baseline zip. This recipe pins the recorded digest, excludes credential gates, and is not a claim that the historical invocation used identical arguments.

```powershell
pwsh -NoProfile -File scripts/multi-plugin-docker-smoke-test.ps1 -LidarrImage 'ghcr.io/hotio/lidarr:pr-plugins-3.1.2.4913@sha256:ae0b3b14769fdfeb73fe5d9e61ebcda04edf202244bcbd6323d2fe1381154f57' -ContainerName lidarr-audit-20260905-qobuz-reproduction -Port 18769 -PluginZip 'qobuzarr=../qobuzarr/artifacts/packages/qobuzarr-0.1.0-dev-net8.0.zip' -WorkRoot artifacts/audit-20260905/reproduction/smoke
```

**Verdict: APPROVE the narrow authfail import patch only. Review-branch checkpoint; NOT merged; NO plugin repins; NOT full ecosystem acceptance.**
