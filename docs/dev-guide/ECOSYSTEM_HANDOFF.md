# Ecosystem Handoff (Lidarr Plugins)

This file is the "pick up and run" handoff doc for the RicherTunes Lidarr plugin ecosystem.

## Repo Map

| Repo | Path | What it is | What "done" looks like |
|------|------|------------|------------------------|
| Common | `lidarr.plugin.common/` | Shared platform + packaging policy + E2E/CI harness | E2E gates are authoritative; Common additions delete drift |
| Qobuzarr | `qobuzarr/` | Qobuz indexer + download client | Search + download works in Docker, post-restart auth works |
| Tidalarr | `tidalarr/` | Tidal indexer + download client | OAuth works, search + download works in Docker, post-restart auth works |
| Brainarr | `brainarr/` | Import list (AI discovery) | ImportList schema + (optional) LLM gate passes when configured |
| AppleMusicarr | `applemusicarr/` | Apple Music metadata integration (no audio downloads) | Plugin loads; manifest/entrypoint matches what builds ship |

## Ecosystem Philosophy ("Thin Common")

1. **Thin Common**: `lidarr.plugin.common/` owns cross-cutting primitives (HTTP resilience, sanitization primitives, packaging policy, E2E harness). Provider policy stays in plugins.
2. **Delete or don't add**: any new Common utility must delete measurable duplication in at least one plugin within 1-2 PRs.
3. **TDD-first**: prefer hermetic tests + golden fixtures + explicit error codes at the failure site. Avoid "classify by string".
4. **E2E is the truth**: multi-plugin Docker E2E gates are the acceptance criteria.

## Project Management (How We Run Work)

### Multi-Plugin Mindset

Treat changes as "ecosystem changes", not "repo changes". If a change touches a cross-cutting concern (packaging, E2E, auth lifecycle primitives, shared utilities), the work is not done until:
- Common is updated (if applicable)
- Consumer repos are bumped (submodules + `ext-common-sha.txt`)
- E2E bootstrap proves it (or a documented host constraint explains why not)

### PR/Workstream Pattern (Recommended)

1. **Common PR**: add/adjust a primitive/guardrail, with tests (hermetic + golden fixtures where appropriate).
2. **Consumer bumps**: small "pure bump" PRs per plugin repo (no opportunistic edits).
3. **Runtime proof**: run `bootstrap` and archive the manifest for traceability.

If a change is too large, split by dependency direction (Common first), not by file type.

### Decisions and Drift Control

- Prefer "delete then add": when a shared utility exists, delete plugin-local clones rather than keeping two versions.
- Prefer explicit structured failure: set `details.errorCode` at the failure site; avoid "classify by string".
- If you must temporarily diverge, add a baseline with an owner + expiry and file an issue immediately.

### Multi-Agent Coordination

When multiple AIs are active, coordinate by "hot files" and avoid overlap:
- E2E: `scripts/e2e-runner.ps1`, `scripts/lib/e2e-gates.psm1`, `scripts/lib/e2e-json-output.psm1`
- Packaging: `build/PluginPackaging.targets`, `tools/PluginPack.psm1`, package policy tests
- CI: reusable workflows under `.github/workflows/`

Avoid revert/reset workflows. Build on top of merged work, and keep patches minimal and attributable.

## Where the Truth Lives

If you only read 4 things:
- `lidarr.plugin.common/docs/PERSISTENT_E2E_TESTING.md` (how to run real E2E locally)
- `lidarr.plugin.common/scripts/e2e-runner.ps1` (the runner)
- `lidarr.plugin.common/docs/E2E_ERROR_CODES.md` (triage contract + structured details)
- `lidarr.plugin.common/docs/ECOSYSTEM_PARITY_ROADMAP.md` (remaining work)

## E2E Platform (Local)

### What "bootstrap" does

`bootstrap` is designed to prove "real-world correctness":
- Schema registration (plugin discovery)
- Configure (idempotent; can create/update from env vars or pass if already configured)
- Search -> AlbumSearch -> Grab (real releases + queue + audio file validation)
- Persist (restart)
- Revalidation (post-restart auth proof)
- Optional: metadata validation and Brainarr LLM gate (opt-in)

### Running bootstrap

From `lidarr.plugin.common/scripts/`:

```powershell
pwsh ./e2e-runner.ps1 -Gate bootstrap -Plugins 'Qobuzarr,Tidalarr,Brainarr' -EmitJson `
  -LidarrUrl 'http://localhost:8691' `
  -ContainerName 'lidarr-multi-plugin-persist' `
  -ExtractApiKeyFromContainer
```

If you need schema validation locally:

```powershell
pwsh ./validate-manifest.ps1 -ManifestPath ./diagnostics/run-manifest.json
```

### Credentials / Secrets (do not commit)

The E2E harness supports env-var driven configuration. Keep secrets out of logs; the runner redacts aggressively, but don't rely on that.

Typical env vars:
- **Qobuzarr**: `QOBUZARR_AUTH_TOKEN` (required), optional `QOBUZARR_USER_ID`, `QOBUZARR_COUNTRY_CODE`, `QOBUZARR_DOWNLOAD_PATH`
- **Tidalarr**: `TIDALARR_CONFIG_PATH` and/or `TIDALARR_REDIRECT_URL`, optional `TIDALARR_MARKET`, `TIDALARR_DOWNLOAD_PATH`
- **Brainarr (opt-in)**: `BRAINARR_LLM_BASE_URL`, optional `BRAINARR_MODEL`

## CI / Workflow Model

### Cross-repo smoke test

`lidarr.plugin.common` provides a reusable workflow for multi-plugin smoke tests:
- `.github/workflows/multi-plugin-smoke-test.yml` (reusable)

Calling repos (Qobuzarr/Tidalarr/Brainarr) should have thin wrapper workflows that `uses:` the Common workflow.

Required secrets in each calling repo:
- `CROSS_REPO_PAT` (PAT with access to the needed repos; required because the reusable workflow checks out other repos)

### Docs-only vs code PRs

CI is hardened so docs-only PRs still report required checks quickly, while code changes run full tests.

## Packaging Policy (Do Not Guess)

Packaging/host-boundary mistakes cause the worst failures (TypeLoadException / MissingMethod / ABI mismatch). Use the Common policy and its packaging preflight checks.

Key invariants:
- `Lidarr.Plugin.Abstractions.dll` must be byte-identical across all plugin packages used together.
- Forbidden host assemblies must not leak into plugin bundles (enforced by packaging preflight + tests).

## Submodule Discipline (ext/)

Most plugins consume Common via a submodule under `ext/` and track the commit in `ext-common-sha.txt`.

Rules:
- Never "fix" Common by editing inside `ext/` directly; fix upstream `lidarr.plugin.common/` and bump.
- After updating the submodule, update `ext-common-sha.txt` to match.
- Keep submodule bump PRs pure (no opportunistic changes).

## Known Host Constraint

Multi-plugin hosting is still impacted by an upstream Lidarr AssemblyLoadContext lifecycle issue (tracked upstream). The E2E manifest records host capabilities (ALC fix detection) so failures are triageable.

## Guardrails That Prevent Regression

- Explicit error codes at the failure site (no classifier guessing).
- Golden fixtures under `scripts/tests/fixtures/golden-manifests/` lock down output shape.
- Doc/code sync tripwire tests prevent "docs drift".
- `scripts/parity-lint.ps1` (when enabled) flags re-inventing Common utilities in plugins.

## What to Do Next (Typical Next PR Shapes)

Pick one and keep it small:
- Delete a duplicated utility from a plugin because Common now owns it.
- Add a Common primitive only if it deletes duplication in a plugin immediately.
- Add/extend golden fixtures when a new failure mode becomes important.
- Improve E2E structured details for a single error code (with fixtures + tests).
