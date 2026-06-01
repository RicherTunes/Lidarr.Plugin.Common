# Reusable Workflow Proposals

**Generated**: 2026-05-23
**Agent**: ci-cd-agent (Phase 1.5)
**Status**: PROPOSAL ONLY — do not implement without design review

---

## Background

A scan of all 5 ecosystem repos (brainarr, qobuzarr, tidalarr, applemusicarr,
lidarr.plugin.common) identified the following patterns repeated 3+ times across
repos. Extracting these into reusable workflows in `lidarr.plugin.common` would
eliminate drift, centralise version bumps, and reduce per-repo CI YAML by ~40%.

---

## Candidate 1: `setup-plugin-dotnet.yml`

**Occurrences**: 42+ identical blocks across 5 repos
**Pattern identified in**: every CI, test-and-coverage, and nightly workflow

### Current duplicated block (verbatim example from qobuzarr/ci.yml):
```yaml
- name: Setup .NET
  uses: actions/setup-dotnet@v4
  with:
    dotnet-version: ${{ env.DOTNET_VERSION }}

- name: Cache NuGet packages
  uses: actions/cache@v4
  with:
    path: ~/.nuget/packages
    key: ${{ runner.os }}-nuget-${{ hashFiles('**/*.csproj', '**/Directory.Packages.props', '**/NuGet.config') }}
    restore-keys: |
      ${{ runner.os }}-nuget-
```

### Proposed reusable workflow: `.github/workflows/setup-plugin-dotnet.yml`
```yaml
# PROPOSAL — not yet implemented
on:
  workflow_call:
    inputs:
      dotnet-version:
        type: string
        default: '8.0.x'
      cache-key-extra:
        type: string
        default: ''
```

### Benefits (Candidate 1)
- Single place to bump `actions/setup-dotnet` and `actions/cache` versions
- Enforces consistent NuGet cache key hash pattern
- Eliminates the `8.0.x` vs `8.0.404` inconsistency (brainarr vs qobuzarr)

### Risks / blockers (Candidate 1)
- Composite actions cannot currently set env vars visible to subsequent steps in the caller;
  a reusable workflow (separate job) solves this but adds job-level overhead and requires
  artifact handoff for build outputs.
- Assess whether inline composite action (`uses: ./actions/setup-plugin-dotnet`) is
  preferred over a separate `workflow_call` workflow.

---

## Candidate 2: `init-common-submodule.yml`

**Occurrences**: 42+ across all plugin repos
**Pattern identified in**: every CI, packaging-gates caller, nightly workflows

### Current duplicated block (verbatim example from tidalarr/ci.yml):
```yaml
- name: Init Common submodule
  shell: bash
  run: |
    git config --local url.https://github.com/.insteadOf git@github.com:
    git submodule sync -- ext/Lidarr.Plugin.Common
    git submodule update --init --depth=1 -- ext/Lidarr.Plugin.Common

- name: Assert Common submodule initialized
  shell: bash
  run: |
    if [ ! -d "ext/Lidarr.Plugin.Common/scripts" ]; then
      echo "::error::FATAL: ext/Lidarr.Plugin.Common/scripts not found"
      exit 1
    fi
    SHA=$(git -C ext/Lidarr.Plugin.Common rev-parse --short HEAD 2>/dev/null || echo "<unknown>")
    echo "Common submodule initialized at $SHA"
```

### Note on existing art
`brainarr` already uses `./.github/actions/init-common-submodule` (a local composite
action). This pattern should be **promoted to Common** so all repos share it.

### Proposed: Promote `brainarr`'s `init-common-submodule` composite action to Common
- Path: `lidarr.plugin.common/.github/actions/init-common-submodule/action.yml`
- All plugin repos reference it as:
  `uses: RicherTunes/Lidarr.Plugin.Common/.github/actions/init-common-submodule@<SHA>`
- Inputs: `token` (optional, for private submodules)

### Benefits (Candidate 2)
- Eliminates 40+ copies of identical submodule init + assert logic
- SHA drift assertion is centralised (update once when assertion logic improves)
- Consistent depth (`--depth=1`) and URL rewrite across all repos

### Risks / blockers (Candidate 2)
- Composite actions from external repos require a checkout step; the action must be
  accessible before the submodule it initialises is available. Use
  `actions/checkout@<SHA> + uses:` referencing the action from the remote ref directly.

---

## Candidate 3: `parity-lint-job.yml`

**Occurrences**: 5 repos, each with an identical `parity-lint` job
**Pattern identified in**: brainarr/ci.yml, qobuzarr/ci.yml, tidalarr/ci.yml,
applemusicarr/ci.yml, lidarr.plugin.common/ci.yml

### Current pattern (composite of all repos):
```yaml
parity-lint:
  name: Parity Lint
  runs-on: ubuntu-latest
  steps:
    - uses: actions/checkout@<ver>
      with: { submodules: false }
    - name: Init Common submodule
      # (6-12 lines, varies per repo)
    - name: Run Parity Lint
      shell: pwsh
      run: |
        & "ext/Lidarr.Plugin.Common/scripts/parity-lint.ps1" -Mode ci -RepoPath ...
    - name: Ecosystem parity lint (version contract)   # Added Phase 1.5
      shell: pwsh
      run: |
        pwsh -NoProfile -File ext/Lidarr.Plugin.Common/scripts/ecosystem-parity-lint.ps1 ...
```

### Proposed reusable workflow: `.github/workflows/parity-lint.yml`
```yaml
# PROPOSAL — not yet implemented
on:
  workflow_call:
    inputs:
      submodules-token-secret-name:
        type: string
        default: 'SUBMODULES_TOKEN'
      extra-checks:
        type: string
        default: 'VersionContract'
```

### Benefits (Candidate 3)
- Single PR to update both lint scripts for all 5 repos
- Guarantees all repos run identical lint logic (no subset drift)
- Phase 1.5's `ecosystem-parity-lint -Check VersionContract` step needs to be
  maintained in 4 separate files today; one reusable workflow fixes this

### Risks / blockers (Candidate 3)
- The `parity-lint` job has repo-specific submodule token secret names
  (`SUBMODULES_TOKEN` in brainarr/applemusicarr, `CI_PAT` in qobuzarr, no token
  in tidalarr). A `secrets: inherit` approach or explicit secret mapping is needed.

---

## Candidate 4: `extract-lidarr-assemblies-job.yml`

**Occurrences**: 40+ in brainarr alone, 15+ in applemusicarr, 8+ in qobuzarr
**Pattern identified in**: ci.yml, docker-e2e.yml, nightly.yml, test-and-coverage.yml

### Current pattern (brainarr ci.yml, `prepare-lidarr` job):
Large block pulling `ghcr.io/hotio/lidarr:pr-plugins-3.1.2.4913`, extracting DLLs,
uploading as artifact. This pattern is copy-pasted across ~20+ workflow files.

### Proposed reusable workflow: `.github/workflows/prepare-lidarr-assemblies.yml`
```yaml
# PROPOSAL — not yet implemented
on:
  workflow_call:
    inputs:
      lidarr-docker-version:
        type: string
        default: 'pr-plugins-3.1.2.4913'
      output-path:
        type: string
        default: 'ext/Lidarr-docker/_output/net8.0'
      artifact-name:
        type: string
        default: 'lidarr-assemblies'
```

### Benefits (Candidate 4)
- Docker version bumps happen in one place (currently requires 20+ file edits)
- Assembly extraction script variations (brainarr vs applemusicarr) consolidated
- `scripts/extract-lidarr-assemblies.sh` already exists in applemusicarr and brainarr;
  the reusable workflow would call Common's canonical version

### Risks / blockers (Candidate 4)
- `prepare-lidarr` in brainarr is a distinct upstream job with concurrency control;
  the artifact handoff pattern must be preserved
- Assembly output path varies (`ext/Lidarr-docker/_output/net8.0` vs `ext/Lidarr/_output/net8.0`)
  — needs normalisation first

---

## Implementation Priority

| Candidate | Impact | Effort | Priority |
|-----------|--------|--------|----------|
| `init-common-submodule` composite action | High (42+ instances) | Low (action exists in brainarr) | **P1** |
| `parity-lint-job.yml` | High (5 repos) | Medium | **P1** |
| `setup-plugin-dotnet.yml` | Medium (version drift) | Low | P2 |
| `extract-lidarr-assemblies-job.yml` | High (Docker version drift) | High | P2 |

---

## Deferred Items

- **`build-and-test.yml`**: Each repo's build+test job is too bespoke (different
  solution files, test filters, coverage gates) to consolidate without a larger
  design effort. Deferred to Phase 2.
- **Matrix definitions**: The `os: [ubuntu-latest, windows-latest, macos-latest]`
  matrix in brainarr/ci.yml is unique to that repo. Not a cross-repo pattern.
