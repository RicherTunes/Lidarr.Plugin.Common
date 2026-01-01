# E2E Hardening Roadmap (Preferred IDs + Cold Start)

This document tracks incremental hardening work for the Lidarr E2E runner in `scripts/`.

Scope: determinism, safety, and debuggability of E2E runs across multiple plugins and multiple Lidarr instances.

## Invariants (non-negotiable)

- Best-effort state: corrupted/missing preferred-ID state must **never** fail an E2E run.
- No user-controlled matching: never select components based on `name` (it’s user-editable).
- Preferred IDs are strict: a stored ID is only accepted if it resolves to the expected plugin (`implementationName == PluginName`).
- No secrets in state: preferred-ID state stores **IDs only** (no tokens, no URLs, no credentials).
- Safe persistence: only persist IDs when resolution is known-safe (`preferredId`, `created`, `implementationName`, `implementation`).

## Implemented (current state)

- Preferred component IDs stored in `.e2e-bootstrap/e2e-component-ids.json` (schema v2, instance-namespaced).
- Gates resolve components via `Find-ConfiguredComponent` (no more `name -like "*$plugin*"` discovery).
- ImportList gate supports `-ImportListId` and does not wildcard-match by name.
- CI cold-start assertions:
  - Case A (no secrets): Configure should SKIP and **not create plugin-specific components**.
  - Case B (with secrets): Configure should create or confirm components.
  - Warm-run assertion: Configure should **prefer stored IDs** when present.

## Next steps (TDD-first)

### P0 (high value)
- [ ] Add `-ComponentIdsInstanceSalt` / `E2E_COMPONENT_IDS_INSTANCE_SALT` so users can avoid collisions when reusing the same container name against different config dirs.
- [ ] Consider tightening “safe persistence” (keep `created` + `preferredId` + `implementationName`; only keep `implementation` if it’s actually reachable in practice).

### P1 (correctness hardening)
- [ ] Record Lidarr host fingerprint (`lidarrVersion`, `branch`, `imageDigest`) into the preferred-ID state entries (diagnostics only; no effect on selection).
- [ ] Reduce/disable fuzzy fallback in `Select-ConfiguredComponent` (flag-gated), and add explicit tests for “no accidental selection”.

### P2 (concurrency + usability)
- [ ] Add state-write lock backoff tuning via env var (still best-effort).
- [ ] Add `E2E_INSTANCE_KEY` override for power users (explicit key beats heuristics).

## References

- Preferred IDs module: `scripts/lib/e2e-component-ids.psm1`
- Preferred IDs doc: `docs/dev-guide/E2E_PREFERRED_COMPONENT_IDS.md`
- Runner: `scripts/e2e-runner.ps1`
- Gates: `scripts/lib/e2e-gates.psm1`
- CI workflow: `.github/workflows/e2e-bootstrap.yml`

