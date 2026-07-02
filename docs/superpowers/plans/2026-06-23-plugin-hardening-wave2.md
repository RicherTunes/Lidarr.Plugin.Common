# Plugin Hardening Wave 2 Implementation Plan

<!-- docval:ignore-script-refs -->

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Pay the next layer of technical debt across Brainarr, Qobuzarr, Tidalarr, and Lidarr.Plugin.Common without destabilizing the already-verified Tidal token-renewal fix.

**Architecture:** Treat Common as the source of shared CI/version policy and keep plugin repos as thin callers. Split work into a low-risk mechanical lane and a risky lane; risky changes require red/green TDD evidence plus adversarial review before integration.

**Tech Stack:** .NET 8, PowerShell 7, Bash, GitHub Actions, Gitea Actions, Lidarr.Plugin.Common shared scripts and TestKit.

## Global Constraints

- Common package version for this wave is `1.18.0-dev`.
- Plugin minimum host version is `3.0.0.4855`.
- Host Docker tag for full-host validation is `nightly-3.1.3.4970`.
- PR/default CI should remain fast and hostless where host assemblies are unavailable.
- Full-host Docker/E2E lanes are manual or scheduled until runner networking and host assemblies are reliable.
- Do not revert pre-existing local changes, including `.glm-delegate-diff/` artifacts or Common `src/packages.lock.json`.
- Risky lane changes require TDD red/green evidence and adversarial review.

---

### Task 1: Common Version And Lint-Gate Foundation

**Files:**
- Modify: `VERSION`
- Modify: `README.md`
- Modify: `docs/ECOSYSTEM_VERSION_CONTRACT.md`
- Modify: `docs/CI_REUSABLE_WORKFLOWS.md`
- Modify: `scripts/parity-spec.json`
- Create: `scripts/ci/run-plugin-lint-gates.ps1`
- Test: `scripts/tests/Test-RunPluginLintGates.ps1`

**Interfaces:**
- Consumes: existing plugin-local Common submodules and `lint-date-parsing.ps1`, `lint-sync-over-async.ps1`, `ecosystem-parity-lint.ps1`.
- Produces: one Common-owned lint runner callable by GitHub and Gitea plugin workflows.

- [ ] Add a Pester test proving the new lint runner invokes the three expected gates with `-Check VersionContract`.
- [ ] Add `scripts/ci/run-plugin-lint-gates.ps1` with `-RepoPath`, `-CommonRoot`, and `-Mode`.
- [ ] Align Common `VERSION`, docs, and version-contract text with `1.18.0-dev` and `Directory.Build.props`.
- [ ] Run the new test and all three plugin lint gates.

### Task 2: Plugin CI Callers Use Common Lint Runner

**Files:**
- Modify: `brainarr/.github/workflows/ci.yml`
- Modify: `brainarr/.gitea/workflows/ci.yml`
- Modify: `qobuzarr/.github/workflows/ci.yml`
- Modify: `qobuzarr/.gitea/workflows/ci.yml`
- Modify: `tidalarr/.github/workflows/ci.yml`
- Modify: `tidalarr/.gitea/workflows/ci.yml`
- Modify: plugin-local `.github/actions/init-common-submodule/action.yml` files only if needed to keep behavior aligned with Common.

**Interfaces:**
- Consumes: `ext/Lidarr.Plugin.Common/scripts/ci/run-plugin-lint-gates.ps1`.
- Produces: equivalent GitHub/Gitea lint behavior with less duplicated YAML drift.

- [ ] Replace copied lint gate steps with the Common runner.
- [ ] Preserve GitHub-only workflow gating and Gitea PowerShell bootstrap constraints.
- [ ] Validate workflow YAML with `actionlint` and parse/extract shell blocks.
- [ ] Run the Common lint runner in each plugin repo.

### Task 3: Brainarr Low-Risk Packaging And Docs Fixes

**Files:**
- Modify: `packaging/expected-contents.txt`
- Modify: `scripts/verify-local.ps1`
- Modify: `build.ps1`
- Modify: `build.sh`
- Modify: `scripts/check-docs-consistency.ps1`
- Modify: `scripts/check-docs-consistency.sh`
- Modify: `.github/scripts/generate-release-notes.sh`
- Modify: `VERSIONING.md`, selected release/docs pages, provider docs generated from current code.

**Interfaces:**
- Consumes: merged-DLL packaging policy from Common `PluginPackaging.targets`.
- Produces: Brainarr docs and package expectations that match `Lidarr.Plugin.Brainarr.dll` plus `plugin.json`/`manifest.json`.

- [ ] Fix docs checks to read `minHostVersion`, not `minimumVersion`, and make the Bash checker executable logic instead of a literal escaped line.
- [ ] Stop packaging from mutating tracked root `manifest.json`; generate package-local metadata instead.
- [ ] Align expected package contents with merged-DLL policy.
- [ ] Update docs/provider metadata to match current provider enum.
- [ ] Run docs consistency checks, packaging tests, and local CI.

### Task 4: Tidalarr Low-Risk CI, CLI, Packaging, And Docs Fixes

**Files:**
- Modify: `scripts/ci.ps1`
- Modify: `src/Tidalarr.HostBridge/Tidalarr.HostBridge.csproj`
- Modify: `tests/Tidalarr.Tests/CLI/CLIDiagnosticsTests.cs`
- Modify: `tests/Tidalarr.Tests/CLI/CLIArgParsingTests.cs`
- Modify: `README.md`
- Modify: `wiki/Authentication.md`
- Modify: selected CI/package docs.

**Interfaces:**
- Consumes: verified token-renewal implementation and `PluginPack`.
- Produces: net8 CLI tests/docs, canonical package artifact path, and documented hostless/full-host split.

- [ ] Update stale CLI `net9.0` references to `net8.0`.
- [ ] Make `PluginPack` the canonical packaging path and avoid manual ZIP drift.
- [ ] Document durable token store vs generic token manager separation and proactive refresh behavior.
- [ ] Align host assembly fallback between scripts and HostBridge project.
- [ ] Run Tidal hostless CI, focused CLI tests, package checks, and fast solution tests.

### Task 5: Qobuzarr Low-Risk Version, Docs, And Fallback Hygiene

**Files:**
- Modify: `build.ps1`
- Modify: `build.sh`
- Modify: `download-lidarr-assemblies.ps1`
- Modify: `download-lidarr-assemblies.sh`
- Modify: `Directory.Packages.props`
- Modify: selected README/wiki/docs references to stale host/auth values.

**Interfaces:**
- Consumes: current live auth stack under `src/Authentication` and `src/API`.
- Produces: host version and docs aligned to `3.0.0.4855` / `nightly-3.1.3.4970`.

- [ ] Align host version constants and docs.
- [ ] Correct stale auth docs that mention nonexistent exception properties.
- [ ] Update fallback package comments/versions without changing production submodule mode.
- [ ] Run parity, focused docs/auth tests, and fast solution tests.

### Task 6: Risky Source Cleanup Lane

**Files:**
- Candidate deletes or obsoletes: `qobuzarr/src/Services/AuthTokenManager.cs`, `qobuzarr/src/Services/Interfaces/IQobuzAuthService.cs`, `qobuzarr/src/Core/QobuzApiService.cs`, duplicate auth exception files, and their tests/docs/scripts.
- Candidate Common packaging internals after plugin expectations are green.

**Interfaces:**
- Consumes: test coverage from live auth/API services.
- Produces: smaller source tree with no production-dead auth/API leftovers.

- [ ] Write failing tests or characterization tests proving live behavior does not depend on candidate-dead classes.
- [ ] Verify RED for each removal/obsolete behavior.
- [ ] Remove or obsolete the smallest safe surface.
- [ ] Verify GREEN with targeted and solution tests.
- [ ] Run adversarial code review before merging this lane.
