# E2E Hardening Roadmap (Preferred IDs + Cold Start)

This document tracks incremental hardening work for the Lidarr E2E runner in `scripts/`.

Scope: determinism, safety, and debuggability of E2E runs across multiple plugins and multiple Lidarr instances.

## Invariants (non-negotiable)

- Best-effort state: corrupted/missing preferred-ID state must **never** fail an E2E run.
- No user-controlled matching: never select components based on `name` (it’s user-editable).
- Preferred IDs are strict: a stored ID is only accepted if it resolves to the expected plugin (`implementationName == PluginName`).
- No secrets in state: preferred-ID state stores **IDs only** (no tokens, no URLs, no credentials).
- Safe persistence: only persist IDs when resolution is known-safe (`preferredId`, `created`, `implementationName`, `implementation`).
- Ambiguity is a failure: if multiple configured components match a plugin, gates must **FAIL** (do not guess).

## Implemented (current state)

- Preferred component IDs stored in `.e2e-bootstrap/e2e-component-ids.json` (schema v2, instance-namespaced).
- Gates resolve components via `Find-ConfiguredComponent` (no more `name -like "*$plugin*"` discovery).
- Component selection reports resolution strategy and candidate IDs (for triage), and gates fail loudly on ambiguous matches.
- Fuzzy selection can be disabled via `-DisableFuzzyComponentMatch` / `E2E_DISABLE_FUZZY_COMPONENT_MATCH=1` (enabled by default in CI).
- ImportList gate supports `-ImportListId` and does not wildcard-match by name.
- CI cold-start assertions:
  - Case A (no secrets): Configure should SKIP and **not create plugin-specific components**.
  - Case B (with secrets): Configure should create or confirm components.
  - Warm-run assertion: Configure should **prefer stored IDs** when present.
- CI schema validation:
  - `scripts/validate-manifest.ps1` validates `run-manifest.json` against `docs/reference/e2e-run-manifest.schema.json`.
  - Cold-start assertions are centralized in `scripts/ci/assert-cold-start.ps1` (fixture-tested).
- CI strict modes:
  - `scripts/ci/validate-manifest-ci.ps1 -Strict` treats “no validator available” as a hard failure in CI cold-start runs.
  - `-StrictPrereqs` / `STRICT_E2E=1` converts credential-related SKIPs into FAILs for credentialed gates (prevents “silent green”).
- Golden manifest fixtures:
  - `scripts/tests/fixtures/golden-manifests/*.json` + `scripts/tests/Test-GoldenManifests.ps1` prevent schema/semantics drift for common outcomes:
    - PASS
    - `E2E_AUTH_MISSING`
    - `E2E_COMPONENT_AMBIGUOUS`
    - host bug classification (`hostBugSuspected.classification = ALC`)

## Next steps (TDD-first)

### P0 (high value)
- [x] Add `-ComponentIdsInstanceSalt` / `E2E_COMPONENT_IDS_INSTANCE_SALT` so users can avoid collisions when reusing the same `(LidarrUrl + ContainerName)` against different config directories/instances.
- [x] Prevent unsafe persistence: only persist IDs when resolution is known-safe (`preferredId`, `created`, `implementationName`, `implementation`).
- [x] Make ambiguity loud: if multiple configured components match a plugin, fail the gate and include candidate IDs for debugging.
- [ ] Consider tightening “safe persistence” further: drop `implementation` if it proves unnecessary in practice (keep `created` + `preferredId` + `implementationName`).

### P0.1 (manifest provenance)
- [x] Emit `results[].details.componentResolution` in the JSON manifest (v1.2+) so failures are diagnosable without reading runner logs.
- [x] Add `matchedOn` enum to make resolution strategy unambiguous for diagnostics.
- [x] Emit factual preferred-ID persistence outcome under the top-level `componentIds` block (`persistedIdsUpdateAttempted` / `persistedIdsUpdated` / `persistedIdsUpdateReason`).

#### Contract: `results[].details.componentResolution`
This is **additive**. Keep existing booleans like `details.indexerFound` / `details.downloadClientFound` for backward compatibility.

When a gate resolves components (Configure/Search/AlbumSearch/Grab/Revalidation/PostRestartGrab/ImportList), the manifest should include:

```json
{
  "details": {
    "componentResolution": {
      "indexer": {
        "selectedId": 101,
        "strategy": "preferredId",
        "matchedOn": "preferredId",
        "candidateIds": [101],
        "safeToPersist": true
      },
      "downloadClient": {
        "selectedId": 201,
        "strategy": "implementationName",
        "matchedOn": "implementationName",
        "candidateIds": [201],
        "safeToPersist": true
      },
      "importList": {
        "selectedId": 301,
        "strategy": "created",
        "matchedOn": "created",
        "candidateIds": [301],
        "safeToPersist": true
      }
    }
  },
  "componentIds": {
    "persistenceEligible": true,
    "persistedIdsUpdateAttempted": true,
    "persistedIdsUpdated": true,
    "persistedIdsUpdateReason": "written"
  }
}
```

Invariants:
- `candidateIds` is always an array; defaults to `[selectedId]` when known.
- `safeToPersist` MUST be `false` for ambiguous/fuzzy/no-match strategies.
- `safeToPersist` MUST be `false` when `selectedId` is null, or when `candidateIds` contains multiple values (even if the strategy string is "safe").
- Ambiguity is a **failure**: outcome MUST be `failed` with `errorCode=E2E_COMPONENT_AMBIGUOUS` and `candidateIds` populated for triage.
- `matchedOn` MUST be a normalized enum derived from `strategy` (see mapping rules below).
- Preferred-ID persistence outcome is reported under the top-level `componentIds` block; `persistedIdsUpdated` MUST be `true` only when preferred-ID state was actually written (not merely eligible).

#### `matchedOn` enum values

| Value | Meaning | `safeToPersist` |
|-------|---------|-----------------|
| `preferredId` | Resolved via stored preferred ID | `true` |
| `implementationName` | Matched by plugin implementation name | `true` |
| `implementation` | Matched by implementation class | `true` |
| `created` | Component was just created | `true` |
| `none` | No match found, ambiguous, or null selectedId | `false` |

Mapping rules (minimal):
- `strategy` comes from runner resolution (`preferredId`, `created`, `implementationName`, `implementation`, `fuzzy`, `none`, `ambiguous*`).
- `matchedOn` is derived from `strategy`:
  - If `strategy` is one of `preferredId`, `implementationName`, `implementation`, `created` → `matchedOn` = same value.
  - If `strategy` starts with `ambiguous` → `matchedOn` = `none`.
  - If `selectedId` is null → `matchedOn` = `none`.
  - Otherwise → `matchedOn` = `none`.
- `safeToPersist` is true only for: `preferredId`, `created`, `implementationName`, `implementation`.
- Note: `updated` is an action outcome, not a selection strategy — excluded from safe strategies.

### P1 (correctness hardening)
- [x] Record Lidarr host fingerprint (`lidarrVersion`, `branch`, `imageTag`, `imageDigest`, `containerId`, `containerStartedAt`) into the preferred-ID state entries (diagnostics only; no effect on selection).
- [x] Reduce/disable fuzzy fallback in `Select-ConfiguredComponent` (flag-gated), and add explicit tests for “no accidental selection”.

### P2 (concurrency + usability)
- [x] Add state-write lock backoff tuning via env var (still best-effort).      
- [x] Add `E2E_INSTANCE_KEY` override for power users (explicit key beats heuristics).

## References

- Preferred IDs module: `scripts/lib/e2e-component-ids.psm1`
- Preferred IDs doc: `docs/dev-guide/E2E_PREFERRED_COMPONENT_IDS.md`
- Runner: `scripts/e2e-runner.ps1`
- Gates: `scripts/lib/e2e-gates.psm1`
- CI workflow: `.github/workflows/e2e-bootstrap.yml`
