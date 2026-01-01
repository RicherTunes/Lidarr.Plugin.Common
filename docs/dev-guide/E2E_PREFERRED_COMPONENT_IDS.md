# E2E Preferred Component IDs (Configure Gate Hardening)

## Problem
Lidarr can have multiple configured components per plugin type (indexers, download clients, import lists). If the E2E runner “discovers” components by name/implementation each run, it can:

- pick the wrong component when multiple exist
- become flaky across restarts/reconfigurations
- make cold-start assertions unreliable

Goal: make the E2E runner deterministic by preferring persisted component IDs when available, while remaining best-effort (never breaking runs).

## Invariants (non-negotiable)
- Best-effort: missing/corrupt state must **not** fail the run.
- Prefer-by-ID is strict: a preferred ID is only accepted if it resolves to the expected plugin (`implementationName == PluginName`).
- No secrets: persisted state contains IDs only (no credentials, no URLs).
- State writes must be best-effort (IO failures should not break E2E).

## State File
Default path: `.e2e-bootstrap/e2e-component-ids.json` (override with `E2E_COMPONENT_IDS_PATH` or `-ComponentIdsPath`).

Optional: set `E2E_COMPONENT_IDS_INSTANCE_SALT` (or `-ComponentIdsInstanceSalt`) to avoid collisions when reusing the same `(LidarrUrl + ContainerName)` against different config directories/instances.

Schema (v2, instance-namespaced):
```json
{
  "schemaVersion": 2,
  "instances": {
    "i-abc123def456": {
      "lidarrUrl": "http://localhost:8691",
      "containerName": "lidarr-multi-plugin-persist",
      "lidarrVersion": "3.1.1.4884",
      "updatedAt": "2026-01-01T15:00:00.000Z",
      "plugins": {
        "Qobuzarr": { "indexerId": 101, "downloadClientId": 201 },
        "Tidalarr": { "indexerId": 102, "downloadClientId": 202 },
        "Brainarr": { "importListId": 301 }
      }
    }
  }
}
```

The `instances` key prevents stale IDs from one Lidarr container being reused against another. The runner computes a stable `instanceKey` from `(lidarrUrl + containerName)` and uses that for lookups.

## Resolution Algorithm (per plugin + type)
1. If stored preferred ID exists:
   - pick `item.id == preferredId` **only if** `item.implementationName == PluginName`
2. Else: pick first exact `implementationName == PluginName`
3. Else: pick first exact `implementation == PluginName`
4. Else: fuzzy fallback (backward compat) — should be last resort and safe

Each selection records a `resolution` string in the run manifest (e.g., `preferredId`, `implementationName`, `implementation`, `fuzzy`, `none`).

## Acceptance Tests (TDD)

### Hermetic (unit tests)
File: `scripts/tests/Test-ComponentIds.ps1`
- Reading missing file returns empty state
- Corrupt/invalid JSON returns empty state
- Round-trip preserves IDs
- Preferred ID is strict (wrong plugin name → ignored)
- Fallback order works (implementationName > implementation > fuzzy)
- Write is atomic (`.tmp` then move) and never returns partial JSON
- Different `instanceKey` cannot read IDs (prevents cross-container collisions)

### Runner behavior (hermetic)
File: `scripts/tests/Test-ConfigureGate.ps1`
- If env vars missing and `-ConfigurePassIfAlreadyConfigured` is set:
  - Configure gate returns PASS (no-op) when components already exist
  - `details.componentIds` populated
  - `details.resolution` present for each component kind

### Live E2E assertions (CI)
Workflow: `.github/workflows/e2e-bootstrap.yml`
- Cold-start + no secrets:
  - Configure is SKIP
  - No plugin-specific configured components exist (do **not** assert global counts are 0)
- Cold-start + secrets:
  - Configure is SUCCESS
  - Preferred component ID state is persisted (file exists + valid JSON)
- Warm run with existing state:
  - Configure is PASS (no-op)
  - `preferredIdUsed == true` where IDs exist

## Roadmap (next hardening increments)
- Add `.e2e-bootstrap/` (or at least `e2e-component-ids.json`) to `.gitignore`.
- Remove/limit fuzzy matching to avoid selecting user-named unrelated components.
- Prefer stored IDs in gates beyond Configure (Search/Grab) to self-heal state.
