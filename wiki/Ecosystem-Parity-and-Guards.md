# Ecosystem Parity and Guards

`lidarr.plugin.common` is the single source of truth for every shared concern across the plugin ecosystem (Tidalarr, Qobuzarr, AppleMusicarr, Brainarr). Parity — the guarantee that all plugins follow the same canonical patterns — is enforced mechanically through guard tests shipped in Common's testkit, not by convention alone.

## How parity is tracked

- **[Ecosystem Parity Matrix](../docs/ECOSYSTEM_PARITY_MATRIX.md)** — the single source of truth for per-plugin, per-concern adoption status (packaging, auth lifecycle, concurrency, E2E gates, etc.). Every cell links to source evidence.
- **[Ecosystem Parity Roadmap](../docs/ECOSYSTEM_PARITY_ROADMAP.md)** — current progress toward full structural and behavioral parity, with notes on architecturally-applicable divergences (e.g. AppleMusicarr is metadata-only, so download orchestration axes are N/A).

## How parity is enforced

Common ships an abstract test base that each plugin repo inherits and runs in CI. The guards live in `testkit/Compliance/`:

- [`EcosystemParityTestBase`](../testkit/Compliance/EcosystemParityTestBase.cs) — structural parity checks (correct DI registrations, no forbidden types, file-to-class naming conventions).
- [`EcosystemParityTestBase.BehaviorContracts`](../testkit/Compliance/EcosystemParityTestBase.BehaviorContracts.cs) — behavioral parity checks (no plugin-local forks of Common interfaces, correct shutdown registration, allowed token-store patterns).

Common's own repo exercises these bases in `tests/Compliance/`:

- [`EcosystemParityTestBaseExtensionTests`](../tests/Compliance/EcosystemParityTestBaseExtensionTests.cs) — validates the harness itself with fixture types.
- [`ParityFixtures_CommonNamespace`](../tests/Compliance/ParityFixtures_CommonNamespace.cs) — internalized fixture types used by the extension tests.

## Promotion and version contract

Before a new Common release propagates to all plugins:

- **[Ecosystem Promotion Checklist](../docs/ECOSYSTEM_PROMOTION_CHECKLIST.md)** — the per-plugin verification matrix (submodule bump, build, runtime sandbox, full test suite).
- **[Ecosystem Version Contract](../docs/ECOSYSTEM_VERSION_CONTRACT.md)** — machine-checkable invariants encoded in `scripts/parity-spec.json` and enforced by `ecosystem-parity-lint.ps1` in CI.

## Summary

For plugin authors: if Common already solves a problem, use Common's implementation — the parity guards will catch drift. Start with the [Parity Matrix](../docs/ECOSYSTEM_PARITY_MATRIX.md) to see what is covered, and inherit `EcosystemParityTestBase` in your plugin's test project to run the same checks locally.
