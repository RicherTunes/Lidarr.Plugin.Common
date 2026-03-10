# Ecosystem Parity — Full Alignment Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Achieve total structural and configuration parity across all 4 plugin repos (Qobuzarr, Brainarr, Tidalarr, AppleMusicarr) and Lidarr.Plugin.Common.

**Architecture:** Each plugin repo should converge on the canonical template defined by Common's `templates/plugin-project/` and the patterns established across the most mature repos (Brainarr, Qobuzarr). Gaps are filled repo-by-repo, dimension-by-dimension. Changes are pure config/infra — no business logic changes.

**Tech Stack:** MSBuild props, GitHub Actions YAML, PowerShell scripts, .editorconfig, NuGet config, JSON configs.

---

## Gap Analysis Summary

The following table captures every parity gap identified. Each dimension lists what the "standard" is (derived from Common's templates and the most-aligned repos) and which plugins diverge.

### Legend
- **Q** = Qobuzarr, **B** = Brainarr, **T** = Tidalarr, **A** = AppleMusicarr
- ✅ = matches standard, ❌ = missing/divergent, ⚠️ = partial

| # | Dimension | Standard | Q | B | T | A | Priority |
|---|-----------|----------|---|---|---|---|----------|
| 1 | **Directory.Build.props** | Common template (ILRepack, Version, SourceLink, Analyzers, NoWarn, SourceLink pkg, CPM exclusion) | ✅ | ✅ | ✅ | ❌ Minimal — missing ILRepack, Version mgmt, SourceLink, NoWarn, CPM exclusion | P0 |
| 2 | **Directory.Packages.props** | CPM enabled, standardized versions | ✅ | ✅ | ✅ | ❌ Missing entirely — no centralized package management | P0 |
| 3 | **global.json** | SDK 8.0.100, rollForward: latestFeature | ✅ 8.0.100 | ⚠️ 8.0.0 | ❌ Missing | ✅ 8.0.100 | P1 |
| 4 | **NuGet.config PackageSourceMapping** | nuget.org + lidarr-taglib with proper mapping | ✅ | ✅ | ⚠️ No TagLib mapping? | ⚠️ Has local AppleMusiSharp feed (fine), but check mapping | P1 |
| 5 | **plugin.json schema** | Consistent fields: id, apiVersion, commonVersion, name, version, author, description, homepage, license, tags, minHostVersion, targetFramework, main, rootNamespace | ✅ | ⚠️ Missing: targetFramework, license, tags, homepage, rootNamespace | ⚠️ Has `minimumVersion` (non-standard dup of minHostVersion) | ❌ Missing: commonVersion, author, description, license, tags, rootNamespace; has non-standard `targets` array instead of `targetFramework` string | P1 |
| 6 | **.editorconfig** | Standardized C# rules (UTF-8, LF, 4-space, analyzer severity, nullable) | ✅ | ✅ | ✅ | ⚠️ Different structure — has test relaxations but missing analyzer severity baseline | P2 |
| 7 | **.gitattributes** | LF normalization for code files, binary for DLLs | ✅ | ✅ | ✅ | ⚠️ Has it but content may differ | P2 |
| 8 | **.markdownlint config** | `.markdownlint.yaml` with standard rules | ✅ | ✅ (has .yaml + .yml dup) | ❌ Missing | ⚠️ Uses .jsonc format instead of .yaml | P2 |
| 9 | **.gitleaks.toml** | Secret scanning config | ✅ | ✅ | ❌ Missing | ❌ Missing | P2 |
| 10 | **dependabot.yml** | NuGet + GH Actions + Gitsubmodule with host-boundary ignores | ✅ | ✅ | ✅ | ❌ Missing | P1 |
| 11 | **sha-pin-allowlist.json** | Allowlist for transitional un-pinned workflows | ✅ | ✅ | ✅ | ❌ Missing (may not need entries, but file should exist) | P2 |
| 12 | **Issue templates** | bug_report.yml, feature_request.yml, config.yml (minimum) | ✅ (4 templates) | ✅ (6 templates) | ⚠️ Only tech_debt_task.yml | ❌ None | P2 |
| 13 | **PR template** | pull_request_template.md | ✅ | ✅ | ✅ | ✅ | ✅ |
| 14 | **CODEOWNERS** | @RicherTunes ownership | ⚠️ Check | ✅ | ❌ Missing | ❌ Missing | P2 |
| 15 | **Workflows — Core set** | ci, packaging-gates, verify-pins, gitleaks, dependency-review, codeql, governance, bump-common, nightly, nightly-live, release, test-and-coverage, multi-plugin-smoke-test, notify-failure, submodule-pin, screenshots | See detailed breakdown below | | | | P1 |
| 16 | **FluentValidation version** | 11.12.0 (Common 1.5.0 transitive) — except Brainarr which MUST use 9.5.4 | ✅ 11.12.0 | ✅ 9.5.4 (correct for Brainarr) | ✅ 11.12.0 | ⚠️ Versions in individual csproj, not centralized | P0 |
| 17 | **NLog version** | 5.4.0 (host-coupled) or 6.0.3 (Brainarr-specific) | ✅ 5.4.0 | ⚠️ 6.0.3 (Brainarr-specific, may be intentional) | ✅ 5.4.0 | ⚠️ Check csproj | P1 |
| 18 | **FluentAssertions version** | 6.12.0 or 6.12.2 (MIT, pinned below v7/v8) | ✅ 6.12.2 | ✅ 6.12.0 | ✅ 6.12.2 | ⚠️ Check | P2 |
| 19 | **ILRepack version** | 2.0.34.2 (Common template) or 2.0.44.1 (Brainarr) | ✅ 2.0.34.2 | ✅ 2.0.44.1 | ✅ 2.0.34.2 | ⚠️ Unknown | P2 |
| 20 | **Packaging: expected-contents.txt** | Standardized REQUIRED/FORBIDDEN sections | ✅ | ✅ (in packaging/) | ✅ | ✅ | ✅ |
| 21 | **Scripts: lint-sync-over-async.ps1** | Present in scripts/ | ✅ | ✅ | ✅ | ✅ | ✅ |
| 22 | **Scripts: verify-local.ps1** | Present in scripts/ or root | ✅ | ✅ | ✅ | ✅ | ✅ |
| 23 | **Scripts: update-expected-contents.ps1** | Present in scripts/ | ✅ | ✅ | ✅ | ✅ | ✅ |
| 24 | **Brainarr .markdownlint duplication** | Should have only `.markdownlint.yaml` | ✅ | ❌ Has both .yaml and .yml | N/A | N/A | P3 |
| 25 | **.pre-commit-config.yaml** | Nice-to-have, not mandatory (only Brainarr has it) | — | ✅ | — | — | P3 (future) |
| 26 | **Common SHA pin** | All on same SHA | ✅ b4c66da | ✅ b4c66da | ✅ b4c66da | ✅ b4c66da | ✅ |
| 27 | **Submodule path** | ext/Lidarr.Plugin.Common (PascalCase) | ✅ | ✅ | ✅ | ✅ | ✅ |

### Workflow Parity Detail

**Core workflows every plugin should have:**

| Workflow | Q | B | T | A | Notes |
|----------|---|---|---|---|-------|
| `ci.yml` | ✅ | ✅ | ✅ | ✅ | |
| `packaging-gates.yml` | ✅ | ✅ | ✅ | ✅ | |
| `verify-pins.yml` | ✅ | ✅ | ✅ | ✅ | |
| `gitleaks.yml` | ✅ | ✅ | ✅ | ✅ | |
| `dependency-review.yml` | ✅ | ✅ | ✅ | ✅ | |
| `codeql.yml` | ✅ | ✅ | ✅ | ❌ | A missing |
| `governance.yml` | ✅ | ✅ | ✅ | ✅ | |
| `bump-common.yml` | ✅ | ✅ | ✅ | ✅ | |
| `nightly.yml` | ✅ | ✅ | ✅ | ✅ | |
| `nightly-live.yml` | ✅ | ✅ | ✅ | ✅ | |
| `release.yml` | ✅ | ✅ | ✅ | ✅ | |
| `test-and-coverage.yml` | ✅ | ✅ | ✅ | ❌ | A missing (has `pr-tests.yml` instead) |
| `multi-plugin-smoke-test.yml` | ✅ | ✅ | ✅ | ✅ | |
| `notify-failure.yml` | ✅ | ✅ | ✅ | ❌ | A missing |
| `submodule-pin.yml` | ✅ | ✅ | ✅ | ❌ | A missing |
| `screenshots.yml` | ✅ | ✅ | ✅ | ❌ | A missing (metadata-only, may not need) |

**Brainarr-only extras (not required for parity):** actionlint, dependency-update, digest-drift, docs-consistency, docs-lint, docs-truth-check, link-check, mutation-tests, nightly-perf-stress, plugin-package, pre-commit, registry, release-simple, sanity-build, validate-manifests, wiki-update, workflow-auth-lint.

**Tidalarr extras:** single-plugin-e2e, security.

**AppleMusicarr extras:** docs, pr-tests, sdk-preflight-manual, sdk-preflight-serve-manual, sdk-smoke-manual, tests-private, wiki-sync.

---

## Chunk 1: AppleMusicarr — Critical Build Config Alignment (P0)

AppleMusicarr is the most divergent repo. Its `Directory.Build.props` is 11 lines (vs 72+ in the template) and it has no `Directory.Packages.props` at all.

### Task 1.1: Align AppleMusicarr Directory.Build.props with Common Template

**Files:**
- Modify: `applemusicarr/Directory.Build.props`
- Reference: `lidarr.plugin.common/templates/plugin-project/Directory.Build.props.template`

**Current state (11 lines):**
```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <Deterministic>true</Deterministic>
    <PluginPackagingDisable Condition="'$(Configuration)' == 'Debug' or '$(GITHUB_ACTIONS)' == 'true'">true</PluginPackagingDisable>
  </PropertyGroup>
</Project>
```

- [ ] **Step 1: Read the Common template and current AppleMusicarr file**

Read `lidarr.plugin.common/templates/plugin-project/Directory.Build.props.template` and `applemusicarr/Directory.Build.props`.

- [ ] **Step 2: Replace AppleMusicarr's Directory.Build.props with the aligned version**

Replace the contents with the template, preserving AppleMusicarr's `PluginPackagingDisable` line. The new file should contain:

```xml
<Project>
  <!-- ILRepack Configuration -->
  <PropertyGroup>
    <ILRepackEnabled>false</ILRepackEnabled>
  </PropertyGroup>

  <!-- Version Management -->
  <PropertyGroup>
    <VersionFromFile Condition="'$(VersionFromFile)' == '' And Exists('$(MSBuildThisFileDirectory)VERSION')">$([System.IO.File]::ReadAllText('$(MSBuildThisFileDirectory)VERSION').Trim())</VersionFromFile>
    <Version Condition="'$(Version)' == '' And '$(VersionFromFile)' != ''">$(VersionFromFile)</Version>
    <Version Condition="'$(Version)' == ''">0.1.0-dev</Version>
    <VersionPrefix Condition="'$(VersionPrefix)' == '' And '$(VersionFromFile)' != ''">$([System.Text.RegularExpressions.Regex]::Match('$(VersionFromFile)', '^\d+\.\d+\.\d+').Value)</VersionPrefix>
    <AssemblyVersion Condition="'$(AssemblyVersion)' == '' And '$(VersionPrefix)' != ''">$(VersionPrefix).0</AssemblyVersion>
    <FileVersion Condition="'$(FileVersion)' == '' And '$(VersionPrefix)' != ''">$(VersionPrefix).0</FileVersion>
  </PropertyGroup>

  <!-- SourceLink + Deterministic Builds -->
  <PropertyGroup>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
    <EmbedUntrackedSources>true</EmbedUntrackedSources>
    <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
    <Deterministic>true</Deterministic>
    <PathMap>$(MSBuildProjectDirectory)=/src</PathMap>
  </PropertyGroup>

  <!-- Common Build Settings -->
  <PropertyGroup>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    <SuppressTfmSupportBuildWarnings>true</SuppressTfmSupportBuildWarnings>
  </PropertyGroup>

  <!-- Analyzer Configuration -->
  <PropertyGroup>
    <RunAnalyzersDuringBuild Condition="'$(RunAnalyzersDuringBuild)' == ''">false</RunAnalyzersDuringBuild>
    <EnableNETAnalyzers Condition="'$(EnableNETAnalyzers)' == ''">false</EnableNETAnalyzers>
  </PropertyGroup>

  <!-- Warning Suppressions -->
  <PropertyGroup>
    <NoWarn>$(NoWarn);SA1200;SA1633;SA1101;SA1309;SA1516;CS1591;CS8618;CS8625;CS8604;CS8603;CS8601;CS8602;CS8629;CS8600;CS8619;CS8622</NoWarn>
    <NoWarn>$(NoWarn);MSB3277</NoWarn>
    <NoWarn>$(NoWarn);NU1903</NoWarn>
    <NoWarn>$(NoWarn);CA1305;CA1816;CA1822;CA1848;CA2007</NoWarn>
  </PropertyGroup>

  <!-- SourceLink Package Reference -->
  <ItemGroup>
    <PackageReference Include="Microsoft.SourceLink.GitHub" PrivateAssets="All" />
  </ItemGroup>

  <!-- Exclude external/submodule directories from central package management -->
  <PropertyGroup Condition="$(MSBuildProjectDirectory.Contains('ext\Lidarr')) OR $(MSBuildProjectDirectory.Contains('ext/Lidarr'))">
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>
```

**Note:** The `PluginPackagingDisable` from the old file is NOT carried over because it disabled packaging in CI (`GITHUB_ACTIONS == true`), which conflicts with packaging-gates. If AppleMusicarr needs this for a specific reason, it should be in the plugin csproj with a comment explaining why.

- [ ] **Step 3: Check if AppleMusicarr has a VERSION file; create one if missing**

Run: `cat applemusicarr/VERSION 2>/dev/null || echo "(missing)"`

If missing, create `applemusicarr/VERSION` with content `0.3.0` (matching plugin.json version without pre-release suffix, or the full `0.3.0-beta.2` if preferred).

- [ ] **Step 4: Verify build still works**

Run: `cd D:/Alex/github/applemusicarr && dotnet build -c Release 2>&1 | tail -5`

Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add Directory.Build.props VERSION
git commit -m "build: align Directory.Build.props with Common template

Adds ILRepack config, VERSION-based versioning, SourceLink,
analyzer config, and warning suppressions to match ecosystem standard."
```

### Task 1.2: Create AppleMusicarr Directory.Packages.props

**Files:**
- Create: `applemusicarr/Directory.Packages.props`
- Modify: `applemusicarr/src/AppleMusicarr.Plugin/AppleMusicarr.Plugin.csproj` (remove Version= attributes)
- Modify: `applemusicarr/src/AppleMusicarr.Core/AppleMusicarr.Core.csproj` (remove Version= attributes)
- Modify: `applemusicarr/tests/AppleMusicarr.Core.Tests/AppleMusicarr.Core.Tests.csproj` (remove Version= attributes)
- Reference: `lidarr.plugin.common/templates/plugin-project/Directory.Packages.props.template`

- [ ] **Step 1: Audit all PackageReference Version= attributes across AppleMusicarr csproj files**

Run:
```bash
grep -rn 'PackageReference.*Version=' D:/Alex/github/applemusicarr/src/ D:/Alex/github/applemusicarr/tests/ --include="*.csproj"
```

Collect every package name and version used. This determines the contents of Directory.Packages.props.

- [ ] **Step 2: Create Directory.Packages.props from the Common template, customized for AppleMusicarr's actual packages**

Use the Common template as the base structure but only include packages that AppleMusicarr actually references. Pin versions to match what the csproj files currently use. Add CPM exclusion for `ext/` submodule projects.

- [ ] **Step 3: Remove Version= attributes from all csproj files**

For each csproj, change `<PackageReference Include="Foo" Version="1.2.3" />` to `<PackageReference Include="Foo" />`. The version is now centralized.

- [ ] **Step 4: Verify restore + build**

Run:
```bash
cd D:/Alex/github/applemusicarr
dotnet restore
dotnet build -c Release
```

Expected: Build succeeds with no NU1507 or version warnings.

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/AppleMusicarr.Core.Tests/ --blame-hang-timeout 30s`

Expected: All tests pass.

- [ ] **Step 6: Commit**

```bash
git add Directory.Packages.props src/ tests/
git commit -m "build: add centralized package management (Directory.Packages.props)

Extracts all package versions from individual csproj files into
Directory.Packages.props for single-source-of-truth version pinning.
Matches ecosystem standard used by Qobuzarr, Brainarr, and Tidalarr."
```

---

## Chunk 2: Missing Config Files (P1–P2)

### Task 2.1: Add global.json to Tidalarr

**Files:**
- Create: `tidalarr/global.json`

- [ ] **Step 1: Create global.json**

```json
{
  "sdk": {
    "version": "8.0.100",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  }
}
```

- [ ] **Step 2: Commit**

```bash
git add global.json
git commit -m "build: add global.json to pin .NET SDK 8.0.100"
```

### Task 2.2: Normalize Brainarr global.json SDK version

**Files:**
- Modify: `brainarr/global.json`

- [ ] **Step 1: Update SDK version from 8.0.0 to 8.0.100**

The `8.0.0` pin means the very first 8.0 SDK release. The standard across the ecosystem is `8.0.100` with `rollForward: latestFeature`. Read the file, then update.

- [ ] **Step 2: Commit**

```bash
git add global.json
git commit -m "build: normalize global.json SDK to 8.0.100 (ecosystem parity)"
```

### Task 2.3: Add .gitleaks.toml to Tidalarr and AppleMusicarr

**Files:**
- Create: `tidalarr/.gitleaks.toml`
- Create: `applemusicarr/.gitleaks.toml`
- Reference: `qobuzarr/.gitleaks.toml` (simplest baseline)

- [ ] **Step 1: Read Qobuzarr's .gitleaks.toml as the baseline**

- [ ] **Step 2: Create minimal .gitleaks.toml for Tidalarr**

Adapt the baseline: keep the default ruleset, add allowlist entries only if Tidalarr has fake test keys. The baseline should be:

```toml
[extend]
# Use default gitleaks rules
useDefault = true

[allowlist]
description = "Allowlisted patterns for test data"
paths = [
  '''tests/.*''',
  '''ext/.*''',
]
```

- [ ] **Step 3: Create minimal .gitleaks.toml for AppleMusicarr**

Same structure, adjusted for AppleMusicarr's test paths.

- [ ] **Step 4: Commit each separately**

### Task 2.4: Add .markdownlint.yaml to Tidalarr

**Files:**
- Create: `tidalarr/.markdownlint.yaml`
- Reference: `qobuzarr/.markdownlint.yaml`

- [ ] **Step 1: Copy Qobuzarr's config (it's the standard)**

```yaml
default: true
MD013: false        # Line length
MD033: false        # HTML in markdown
MD041: false        # First line heading
MD024:
  siblings_only: true
MD029:
  style: ordered
MD046:
  style: fenced
```

- [ ] **Step 2: Commit**

### Task 2.5: Add dependabot.yml to AppleMusicarr

**Files:**
- Create: `applemusicarr/.github/dependabot.yml`
- Reference: `tidalarr/.github/dependabot.yml`

- [ ] **Step 1: Read Tidalarr's dependabot.yml as baseline**

- [ ] **Step 2: Create AppleMusicarr's version**

Use the same structure: NuGet weekly, GH Actions weekly, Gitsubmodule weekly. Include the same host-boundary package ignore rules (NLog >=6.0.0, FluentValidation >=12.0.0, Microsoft.Extensions.* >=9.0.0).

- [ ] **Step 3: Commit**

### Task 2.6: Add sha-pin-allowlist.json to AppleMusicarr

**Files:**
- Create: `applemusicarr/.github/sha-pin-allowlist.json`

- [ ] **Step 1: Create empty allowlist (or with relevant entries)**

Check if AppleMusicarr's verify-pins.yml references this file. If so, create with appropriate entries. If no workflows need allowlisting, create with empty entries array:

```json
{
  "entries": []
}
```

- [ ] **Step 2: Commit**

### Task 2.7: Clean up Brainarr's duplicate markdownlint config

**Files:**
- Delete: `brainarr/.markdownlint.yml` (keep `.markdownlint.yaml` as the standard)

- [ ] **Step 1: Verify .markdownlint.yaml has the correct content and .yml is the duplicate**

Read both files. If `.yml` is the duplicate or has a subset of rules, delete it. If `.yml` has unique rules, merge them into `.yaml` first.

- [ ] **Step 2: Delete the duplicate**

- [ ] **Step 3: Commit**

---

## Chunk 3: plugin.json Schema Alignment (P1)

### Task 3.1: Normalize Brainarr plugin.json

**Files:**
- Modify: `brainarr/plugin.json`

**Current state:** Missing `targetFramework`, `license`, `tags`, `homepage`, `rootNamespace`.

- [ ] **Step 1: Read current plugin.json**

- [ ] **Step 2: Add missing standard fields**

Add:
```json
"targetFramework": "net8.0",
"license": "GPL-3.0",
"tags": ["music", "ai", "recommendations", "import-list"],
"homepage": "https://github.com/RicherTunes/Brainarr",
"rootNamespace": "NzbDrone.Core.ImportLists.Brainarr"
```

Preserve all existing fields. Ensure field order matches the ecosystem convention: id, apiVersion, commonVersion, name, version, author, description, homepage, license, tags, minHostVersion, targetFramework, main, rootNamespace, (extras like website, owner, repository, supportUri, changelogUri).

- [ ] **Step 3: Commit**

### Task 3.2: Normalize Tidalarr plugin.json

**Files:**
- Modify: `tidalarr/plugin.json`

**Issues:** Has non-standard `minimumVersion` field (duplicate of `minHostVersion`).

- [ ] **Step 1: Remove `minimumVersion` field**

This is a non-standard duplicate. The canonical field is `minHostVersion`. Check if any CI script or workflow reads `minimumVersion` before removing.

Run: `grep -r "minimumVersion" D:/Alex/github/tidalarr/.github/ D:/Alex/github/tidalarr/scripts/ D:/Alex/github/lidarr.plugin.common/tools/ 2>/dev/null`

- [ ] **Step 2: Verify minHostVersion value**

Current: `"minHostVersion": "3.0.0.4855"` — This is different from the other plugins which use `2.14.2.4786`. Confirm this is intentional (Tidalarr may require a newer host).

- [ ] **Step 3: Commit**

### Task 3.3: Normalize AppleMusicarr plugin.json

**Files:**
- Modify: `applemusicarr/src/AppleMusicarr.Plugin/plugin.json`

**Current state:**
```json
{
  "$schema": "../../ext/Lidarr.Plugin.Common/docs/reference/plugin.schema.json",
  "id": "applemusicarr",
  "name": "Apple Music",
  "version": "0.3.0-beta.2",
  "apiVersion": "1.x",
  "minHostVersion": "2.14.0",
  "main": "AppleMusicarr.Plugin.dll",
  "targets": ["net8.0"]
}
```

**Missing:** commonVersion, author, description, homepage, license, tags, rootNamespace. Has non-standard `targets` array (should be `targetFramework` string).

- [ ] **Step 1: Add missing fields and fix non-standard ones**

```json
{
  "$schema": "../../ext/Lidarr.Plugin.Common/docs/reference/plugin.schema.json",
  "id": "applemusicarr",
  "apiVersion": "1.x",
  "commonVersion": "1.5.0",
  "name": "Apple Music",
  "version": "0.3.0-beta.2",
  "author": "RicherTunes",
  "description": "Apple Music integration for Lidarr - Import lists and metadata from Apple Music",
  "homepage": "https://github.com/RicherTunes/AppleMusicarr",
  "license": "MIT",
  "tags": ["music", "apple-music", "import-list", "metadata"],
  "minHostVersion": "2.14.0",
  "targetFramework": "net8.0",
  "main": "AppleMusicarr.Plugin.dll",
  "rootNamespace": "AppleMusicarr"
}
```

**Note:** Remove `targets` array. If ManifestCheck or any tooling reads `targets`, check first:

Run: `grep -r '"targets"' D:/Alex/github/lidarr.plugin.common/tools/ 2>/dev/null`

- [ ] **Step 2: Commit**

---

## Chunk 4: GitHub Config Parity (P1–P2)

### Task 4.1: Add CODEOWNERS to Tidalarr and AppleMusicarr

**Files:**
- Create: `tidalarr/.github/CODEOWNERS`
- Create: `applemusicarr/.github/CODEOWNERS`

- [ ] **Step 1: Read Brainarr's CODEOWNERS as baseline**

- [ ] **Step 2: Create for both repos**

Minimal content:
```
# Global owners
* @RicherTunes
```

- [ ] **Step 3: Commit each separately**

### Task 4.2: Add standard issue templates to Tidalarr

**Files:**
- Create: `tidalarr/.github/ISSUE_TEMPLATE/bug_report.yml`
- Create: `tidalarr/.github/ISSUE_TEMPLATE/feature_request.yml`
- Create: `tidalarr/.github/ISSUE_TEMPLATE/config.yml`

Tidalarr currently only has `tech_debt_task.yml`. Add the standard templates.

- [ ] **Step 1: Read Qobuzarr's issue templates as baseline**

- [ ] **Step 2: Create adapted versions for Tidalarr**

Copy structure from Qobuzarr, replace project-specific references (Qobuz → Tidal).

- [ ] **Step 3: Commit**

### Task 4.3: Add standard issue templates to AppleMusicarr

**Files:**
- Create: `applemusicarr/.github/ISSUE_TEMPLATE/bug_report.yml`
- Create: `applemusicarr/.github/ISSUE_TEMPLATE/feature_request.yml`
- Create: `applemusicarr/.github/ISSUE_TEMPLATE/config.yml`

- [ ] **Step 1: Create adapted versions for AppleMusicarr**

- [ ] **Step 2: Commit**

### Task 4.4: Add CODEOWNERS to Qobuzarr (if missing)

**Files:**
- Create: `qobuzarr/.github/CODEOWNERS` (if it doesn't exist)

- [ ] **Step 1: Check if it exists**

Run: `cat D:/Alex/github/qobuzarr/.github/CODEOWNERS 2>/dev/null || echo "(missing)"`

- [ ] **Step 2: Create if missing**

- [ ] **Step 3: Commit**

---

## Chunk 5: Missing Workflows — AppleMusicarr (P1)

AppleMusicarr is missing 5 core workflows that all other plugins have.

### Task 5.1: Add codeql.yml to AppleMusicarr

**Files:**
- Create: `applemusicarr/.github/workflows/codeql.yml`
- Reference: `tidalarr/.github/workflows/codeql.yml`

- [ ] **Step 1: Read Tidalarr's codeql.yml**

- [ ] **Step 2: Adapt for AppleMusicarr**

Replace project-specific paths (solution file name, etc.). Keep the same CodeQL configuration.

- [ ] **Step 3: Commit**

### Task 5.2: Add test-and-coverage.yml to AppleMusicarr

**Files:**
- Create: `applemusicarr/.github/workflows/test-and-coverage.yml`
- Reference: `tidalarr/.github/workflows/test-and-coverage.yml`

- [ ] **Step 1: Read Tidalarr's workflow as baseline**

- [ ] **Step 2: Adapt for AppleMusicarr's test projects**

Update test project paths, coverage settings, solution file reference.

- [ ] **Step 3: Commit**

### Task 5.3: Add notify-failure.yml to AppleMusicarr

**Files:**
- Create: `applemusicarr/.github/workflows/notify-failure.yml`
- Reference: `tidalarr/.github/workflows/notify-failure.yml`

- [ ] **Step 1: Copy and adapt**

- [ ] **Step 2: Commit**

### Task 5.4: Add submodule-pin.yml to AppleMusicarr

**Files:**
- Create: `applemusicarr/.github/workflows/submodule-pin.yml`
- Reference: `tidalarr/.github/workflows/submodule-pin.yml`

- [ ] **Step 1: Copy and adapt**

- [ ] **Step 2: Commit**

### Task 5.5: Add screenshots.yml to AppleMusicarr (Optional)

AppleMusicarr is metadata-only so UI screenshots may not apply. Evaluate whether this is needed.

- [ ] **Step 1: Decide if screenshots.yml is relevant for AppleMusicarr**

If the plugin has a Lidarr UI settings page, it should have screenshots. If metadata-only with no UI, skip this task.

- [ ] **Step 2: If relevant, copy from Tidalarr and adapt**

- [ ] **Step 3: Commit (if applicable)**

---

## Chunk 6: Dependency Version Normalization (P1–P2)

### Task 6.1: Audit and align Qobuzarr Directory.Packages.props

**Files:**
- Modify: `qobuzarr/Directory.Packages.props`

**Issues identified:**
- `Lidarr.Plugin.Common` pinned at `1.1.7` (should be `1.2.2` or latest if using NuGet path)
- Some package versions behind template (e.g., `coverlet.collector` at `6.0.2` vs `8.0.0`)
- `BenchmarkDotNet` at `0.13.12` (template says `0.14.0`)
- `FsCheck` at `2.16.5` (template says `2.16.6`)
- `System.IO.Abstractions` at `17.2.3` (template says `21.1.3`)
- `NSubstitute` at `5.1.0` (template says `5.3.0`)

- [ ] **Step 1: Read current file and template side by side**

- [ ] **Step 2: Update divergent versions to match template where safe**

Only update test dependencies and build tools. Do NOT change host-boundary packages (NLog, FluentValidation, Microsoft.Extensions.*).

- [ ] **Step 3: Verify build + tests**

- [ ] **Step 4: Commit**

### Task 6.2: Audit and align Tidalarr Directory.Packages.props

**Files:**
- Modify: `tidalarr/Directory.Packages.props`

**Issues identified:**
- `NLog` at `5.4.0` ✅ (correct for host coupling)
- `FluentValidation` at `11.12.0` ✅
- Missing `ILRepack.Lib.MSBuild.Task` entry (may be in csproj directly)
- `Lidarr.Plugin.Abstractions` and `Lidarr.Plugin.Common` at `1.2.2` (matches template)
- `Newtonsoft.Json` at `13.0.3` ✅ (matches template)

- [ ] **Step 1: Read and compare with template**

- [ ] **Step 2: Add any missing entries, update stale test dependency versions**

- [ ] **Step 3: Verify build + tests**

- [ ] **Step 4: Commit**

### Task 6.3: Audit Brainarr's NLog version

**Files:**
- Review: `brainarr/Directory.Packages.props`

Brainarr uses NLog `6.0.3` while others use `5.4.0`. This needs investigation.

- [ ] **Step 1: Determine if NLog 6.0.3 is intentional for Brainarr**

Check if Brainarr's plugin actually crosses the NLog boundary with the host. Since Brainarr is an ImportList (not indexer/download client), it may not hit the same NLog type-identity issue. Check CLAUDE.md and any host-version coupling tests.

- [ ] **Step 2: Document finding — either align or add comment explaining why it's different**

If NLog 6.x is safe for Brainarr, add a comment to Directory.Packages.props. If not, downgrade to 5.4.0.

- [ ] **Step 3: Commit if changed**

---

## Chunk 7: .editorconfig and .gitattributes Normalization (P2)

### Task 7.1: Normalize AppleMusicarr .editorconfig

**Files:**
- Modify: `applemusicarr/.editorconfig`
- Reference: `qobuzarr/.editorconfig` (closest to standard)

- [ ] **Step 1: Read both files**

- [ ] **Step 2: Merge AppleMusicarr's useful test relaxations into the standard structure**

Keep the standard analyzer severity baseline from other repos, but preserve AppleMusicarr's test-specific relaxations (CA1707, CA1305, CA1310, CA1861).

- [ ] **Step 3: Commit**

### Task 7.2: Normalize .gitattributes across all repos

**Files:**
- Review: all 4 plugin `.gitattributes`

- [ ] **Step 1: Compare all 4 files**

- [ ] **Step 2: Standardize on the most comprehensive version**

All should have: auto text=auto eol=lf, explicit LF for code files (.cs, .csproj, .sln, .json, .yml, .yaml, .md, .txt, .sh, .ps1, .props, .targets), binary for .dll/.exe/.zip/.png.

- [ ] **Step 3: Commit any changes per-repo**

---

## Chunk 8: AppleMusicarr manifest.json Version Discrepancy (P1)

### Task 8.1: Fix version discrepancy between plugin.json and manifest.json

**Files:**
- Review: `applemusicarr/src/AppleMusicarr.Plugin/plugin.json` (v0.3.0-beta.2)
- Review: `applemusicarr/src/AppleMusicarr.Plugin/manifest.json` (v0.3.0-beta.3, targetFramework net6.0)

- [ ] **Step 1: Read manifest.json**

- [ ] **Step 2: Align version with plugin.json and fix targetFramework**

The manifest.json says `net6.0` but the plugin targets `net8.0`. Fix:
- Version: should match plugin.json
- targetFrameworks: should be `["net8.0"]`
- commonVersion: should be `"1.5.0"` (not `"1.0.0-164-g4966bf1"`)

- [ ] **Step 3: Commit**

---

## Chunk 9: Tidalarr Missing Workflows — security.yml Assessment

### Task 9.1: Verify Tidalarr has the `security.yml` workflow and it matches standard

**Files:**
- Review: `tidalarr/.github/workflows/security.yml`

- [ ] **Step 1: Read the file**

Tidalarr has `security.yml` in its workflow list. Verify it's functional and compare with Qobuzarr's equivalent.

- [ ] **Step 2: Document any differences**

---

## Chunk 10: Parity Lint Integration (P2)

### Task 10.1: Add AppleMusicarr to parity-lint.ps1

**Files:**
- Modify: `lidarr.plugin.common/scripts/parity-lint.ps1`

**Current state:** The `Get-PluginRepos` function only scans `qobuzarr`, `tidalarr`, `brainarr`.

- [ ] **Step 1: Read the script**

- [ ] **Step 2: Add `applemusicarr` to the repos list**

In the `Get-PluginRepos` function, add `'applemusicarr'` to the `$repos` array.

- [ ] **Step 3: Run the lint to verify it works**

Run: `pwsh D:/Alex/github/lidarr.plugin.common/scripts/parity-lint.ps1 -AllRepos`

- [ ] **Step 4: Commit**

---

## Execution Order

The chunks should be executed in this order:

1. **Chunk 1** (AppleMusicarr P0 build config) — Foundation, everything else depends on this
2. **Chunk 8** (AppleMusicarr manifest fix) — Quick fix, high impact
3. **Chunk 3** (plugin.json normalization) — Low risk, high parity value
4. **Chunk 2** (Missing config files) — Independent tasks, can be parallelized per-repo
5. **Chunk 4** (GitHub config) — Independent, low risk
6. **Chunk 5** (AppleMusicarr missing workflows) — Requires Chunk 1 first
7. **Chunk 6** (Dependency versions) — Requires careful testing
8. **Chunk 7** (editorconfig/gitattributes) — Cosmetic, low priority
9. **Chunk 9** (Tidalarr security.yml) — Quick review
10. **Chunk 10** (Parity lint update) — Capstone

---

## Validation Checklist (Post-Implementation)

After all chunks are complete, run this validation:

- [ ] All 4 plugins build: `dotnet build -c Release` in each repo
- [ ] All 4 plugins pass tests: `dotnet test --blame-hang-timeout 30s` in each repo
- [ ] Common SHA is identical in all 4 `ext-common-sha.txt` files
- [ ] `parity-lint.ps1 -AllRepos` passes with 0 new violations
- [ ] Each plugin's `packaging-gates.yml` CI passes
- [ ] Each plugin's `verify-pins.yml` CI passes
- [ ] `plugin.json` in all 4 repos has: id, apiVersion, commonVersion, name, version, author, description, homepage, license, tags, minHostVersion, targetFramework, main, rootNamespace
- [ ] All 4 repos have: Directory.Build.props (aligned), Directory.Packages.props, global.json, .editorconfig, .gitattributes, .markdownlint config, .gitleaks.toml, ext-common-sha.txt, packaging/expected-contents.txt
- [ ] All 4 repos have dependabot.yml with host-boundary ignores
- [ ] All 4 repos have: bug_report.yml, feature_request.yml, CODEOWNERS, PR template
- [ ] All 4 repos have core workflow set (ci, packaging-gates, verify-pins, gitleaks, dependency-review, codeql, governance, bump-common, nightly, nightly-live, release, multi-plugin-smoke-test)
