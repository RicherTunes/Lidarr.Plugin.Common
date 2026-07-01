#!/usr/bin/env pwsh
<#
.SYNOPSIS
    F11 Ecosystem CI-contract verifier — cross-repo governance gate.

.DESCRIPTION
    A single maintainer script (run locally/manually against the sibling-repo layout at
    C:\R\Alex\github\) that asserts cross-repo CI/governance invariants the per-repo
    gates cannot see on their own. Catches ecosystem drift such as a plugin that stops
    pinning Common, drops the doc-refs lint, or sprouts undeclared GitHub workflows.

    For each plugin in the manifest the following assertions are evaluated:

      1. Common pin integrity
         ext-common-sha.txt exists, is valid 40-hex+LF, and EQUALS the submodule
         gitlink (`git -C <plugin> ls-tree HEAD ext/Lidarr.Plugin.Common`).

      2. Doc-refs lint wired
         The plugin's .gitea/workflows/ci.yml invokes the doc-refs lint either
         directly (lint-doc-script-refs.ps1) or via the shared runner
         (run-plugin-lint-gates.ps1).

      3. Shared lint runner wired
         The plugin's .gitea/workflows/ci.yml invokes the Common-owned
         run-plugin-lint-gates.ps1 runner.

      4. Verify job wired
         The plugin's .gitea/workflows/ci.yml invokes scripts/verify-local.ps1
         so CI runs build/test/package checks, not lint only.

      5. Workflow-mirror declaration
         Count of .github/workflows/*.yml (+.yaml) files matches the manifest's
         mirrorWorkflows field.

      6. GitHub mirror CI parity
         If mirrorWorkflows > 0, .github/workflows/ci.yml must invoke the same
         shared lint runner, submodule pin guard, secret scan, and verify-local
         merge gate used by Gitea CI.

      7. Workflow content integrity
         Workflow files under .github/workflows and .gitea/workflows are readable,
         non-empty, not obvious per-character corruption, and contain minimal
         workflow structure (`on:` + `jobs:`).

    Cross-plugin SHA agreement is also evaluated: if passing plugins pin different
    Common SHAs, the verifier fails. ALC/package drift is an ecosystem-wide runtime
    risk, not an advisory condition.

    An optional -CheckBranchProtection switch queries the Gitea API for each repo's
    branch protections (requires a Gitea token via git credential fill; off by default).

.PARAMETER EcosystemRoot
    Base directory containing all plugin repos as subdirectories.
    Default: one level above the Common repo root (i.e., the sibling layout at
    C:\R\Alex\github\ when Common is at C:\R\Alex\github\lidarr.plugin.common).

.PARAMETER CI
    Terse output mode: suppress per-field detail, print only one PASS/FAIL line per plugin.

.PARAMETER CheckBranchProtection
    Optional: query Gitea API for branch protections and assert required status checks.
    Requires a token accessible via `git credential fill` for host 192.168.2.59:3001.
    Off by default so the core script runs offline.

.PARAMETER ManifestPath
    Override the manifest file location (default: ecosystem-repos.json in the same
    directory as this script). Intended for test use only.

.PARAMETER DefineFunctionsOnly
    Define pure assertion functions and return without running the main check.
    Used by Test-EcosystemCiContract.ps1 to exercise functions hermetically.

.EXAMPLE
    # Standard local run against sibling layout
    pwsh scripts/ci/verify-ecosystem-ci-contract.ps1

.EXAMPLE
    # Explicit ecosystem root
    pwsh scripts/ci/verify-ecosystem-ci-contract.ps1 -EcosystemRoot C:\R\Alex\github

.EXAMPLE
    # Terse mode + branch-protection check
    pwsh scripts/ci/verify-ecosystem-ci-contract.ps1 -CI -CheckBranchProtection
#>

[CmdletBinding()]
param(
    [string]$EcosystemRoot,

    [switch]$CI,

    [switch]$CheckBranchProtection,

    [string]$ManifestPath,

    [switch]$DefineFunctionsOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ============================================================
# Pure assertion functions (exposed via -DefineFunctionsOnly)
# ============================================================

function Test-CommonPinIntegrity {
    <#
    .SYNOPSIS
        Assert ext-common-sha.txt is valid and matches the submodule gitlink.
    .OUTPUTS
        PSCustomObject { Ok, Sha, Reason }
    #>
    param(
        [string]$PluginDir,
        [string]$PluginName
    )

    $shaFile = Join-Path $PluginDir 'ext-common-sha.txt'

    if (-not (Test-Path -LiteralPath $shaFile)) {
        return [PSCustomObject]@{ Ok = $false; Sha = $null; Reason = 'ext-common-sha.txt missing' }
    }

    # Validate format: exactly 40 lowercase hex + LF or CRLF.
    # Windows checkouts can materialize the one-line sentinel as CRLF while the
    # committed blob remains semantically identical; the SHA payload stays strict.
    $rawBytes = [System.IO.File]::ReadAllBytes($shaFile)
    $hasLf = $rawBytes.Length -eq 41 -and $rawBytes[40] -eq 0x0A
    $hasCrlf = $rawBytes.Length -eq 42 -and $rawBytes[40] -eq 0x0D -and $rawBytes[41] -eq 0x0A
    if (-not ($hasLf -or $hasCrlf)) {
        return [PSCustomObject]@{
            Ok     = $false
            Sha    = $null
            Reason = "ext-common-sha.txt wrong format ($($rawBytes.Length) bytes, expected 40 hex chars plus LF or CRLF)"
        }
    }

    $sentinel = [System.Text.Encoding]::ASCII.GetString($rawBytes, 0, 40)
    if ($sentinel -cnotmatch '^[0-9a-f]{40}$') {
        return [PSCustomObject]@{
            Ok     = $false
            Sha    = $null
            Reason = "ext-common-sha.txt content is not 40 lowercase hex chars: '$sentinel'"
        }
    }

    # Read submodule gitlink (try both casing variants)
    $lsOutput = git -C $PluginDir ls-tree HEAD 'ext/Lidarr.Plugin.Common' 2>$null
    if (-not $lsOutput) {
        $lsOutput = git -C $PluginDir ls-tree HEAD 'ext/lidarr.plugin.common' 2>$null
    }
    if (-not $lsOutput) {
        return [PSCustomObject]@{
            Ok     = $false
            Sha    = $null
            Reason = 'Cannot read submodule gitlink (not a git repo, or ext/Lidarr.Plugin.Common not committed)'
        }
    }
    $gitlinkSha = if ($lsOutput -match '([0-9a-f]{40})') { $Matches[1] } else { $null }
    if (-not $gitlinkSha) {
        return [PSCustomObject]@{
            Ok     = $false
            Sha    = $null
            Reason = "Cannot parse gitlink SHA from ls-tree output: $lsOutput"
        }
    }

    if ($sentinel -ne $gitlinkSha) {
        return [PSCustomObject]@{
            Ok     = $false
            Sha    = $null
            Reason = "Sentinel/gitlink mismatch: ext-common-sha.txt=$sentinel, gitlink=$gitlinkSha"
        }
    }

    return [PSCustomObject]@{ Ok = $true; Sha = $sentinel; Reason = $null }
}

function Test-DocRefsLintWired {
    <#
    .SYNOPSIS
        Assert the plugin's .gitea/workflows/ci.yml invokes the doc-refs lint gate.
    .OUTPUTS
        PSCustomObject { Ok, Reason }
    #>
    param(
        [string]$PluginDir,
        [string]$PluginName
    )

    $ciYml = Join-Path $PluginDir '.gitea/workflows/ci.yml'
    if (-not (Test-Path -LiteralPath $ciYml)) {
        return [PSCustomObject]@{ Ok = $false; Reason = '.gitea/workflows/ci.yml not found' }
    }

    $content = [System.IO.File]::ReadAllText($ciYml)

    # Accept either direct invocation or via the shared runner (which includes it)
    $hasLint = $content -match 'lint-doc-script-refs\.ps1' -or
               $content -match 'run-plugin-lint-gates\.ps1'

    if (-not $hasLint) {
        return [PSCustomObject]@{
            Ok     = $false
            Reason = '.gitea/workflows/ci.yml does not invoke lint-doc-script-refs.ps1 or run-plugin-lint-gates.ps1'
        }
    }

    return [PSCustomObject]@{ Ok = $true; Reason = $null }
}

function Test-SharedPluginLintRunnerWired {
    <#
    .SYNOPSIS
        Assert the plugin's .gitea/workflows/ci.yml invokes the shared Common
        plugin lint runner, not a hand-maintained subset of today's gates.
    .OUTPUTS
        PSCustomObject { Ok, Reason }
    #>
    param(
        [string]$PluginDir,
        [string]$PluginName
    )

    $ciYml = Join-Path $PluginDir '.gitea/workflows/ci.yml'
    if (-not (Test-Path -LiteralPath $ciYml)) {
        return [PSCustomObject]@{ Ok = $false; Reason = '.gitea/workflows/ci.yml not found' }
    }

    $content = [System.IO.File]::ReadAllText($ciYml)
    if ($content -notmatch 'run-plugin-lint-gates\.ps1') {
        return [PSCustomObject]@{
            Ok     = $false
            Reason = '.gitea/workflows/ci.yml does not invoke run-plugin-lint-gates.ps1; hand-wired lint subsets can miss new Common gates'
        }
    }

    return [PSCustomObject]@{ Ok = $true; Reason = $null }
}

function Test-PluginVerifyJobWired {
    <#
    .SYNOPSIS
        Assert the plugin's .gitea/workflows/ci.yml invokes the repo-local
        verify wrapper, so CI runs build/test/package checks and not lint only.
    .OUTPUTS
        PSCustomObject { Ok, Reason }
    #>
    param(
        [string]$PluginDir,
        [string]$PluginName
    )

    $ciYml = Join-Path $PluginDir '.gitea/workflows/ci.yml'
    if (-not (Test-Path -LiteralPath $ciYml)) {
        return [PSCustomObject]@{ Ok = $false; Reason = '.gitea/workflows/ci.yml not found' }
    }

    # Only count the invocation when it appears on a NON-comment line. A commented-out
    # "# run: pwsh scripts/verify-local.ps1" must not satisfy the gate — CI would not actually
    # run verify, so lint-only regressions could slip through while the gate reported "wired".
    $invoked = $false
    foreach ($line in ([System.IO.File]::ReadAllLines($ciYml))) {
        if ($line.TrimStart().StartsWith('#')) { continue }
        if ($line -match 'scripts[/\\]verify-local\.ps1') { $invoked = $true; break }
    }
    if (-not $invoked) {
        return [PSCustomObject]@{
            Ok     = $false
            Reason = '.gitea/workflows/ci.yml does not invoke scripts/verify-local.ps1 on a non-comment line; lint-only CI can miss build/test/package regressions'
        }
    }

    return [PSCustomObject]@{ Ok = $true; Reason = $null }
}

function Test-WorkflowMirrorCount {
    <#
    .SYNOPSIS
        Assert .github/workflows/*.yml count matches the manifest declaration.
    .OUTPUTS
        PSCustomObject { Ok, ActualCount, Reason }
    #>
    param(
        [string]$PluginDir,
        [int]$Expected
    )

    $wfDir = Join-Path $PluginDir '.github/workflows'
    $count  = 0
    if (Test-Path -LiteralPath $wfDir) {
        $ymlFiles  = @(Get-ChildItem -LiteralPath $wfDir -Filter '*.yml'  -File -ErrorAction SilentlyContinue)
        $yamlFiles = @(Get-ChildItem -LiteralPath $wfDir -Filter '*.yaml' -File -ErrorAction SilentlyContinue)
        $count = $ymlFiles.Count + $yamlFiles.Count
    }

    if ($count -ne $Expected) {
        return [PSCustomObject]@{
            Ok          = $false
            ActualCount = $count
            Reason      = ".github/workflows count $count != manifest expected $Expected"
        }
    }

    return [PSCustomObject]@{ Ok = $true; ActualCount = $count; Reason = $null }
}

function Test-GitHubCiMirrorContract {
    <#
    .SYNOPSIS
        Assert declared GitHub mirror CI runs the same core gates as Gitea CI.

    .DESCRIPTION
        Repos with mirrorWorkflows=0 are intentionally Gitea-primary and pass
        vacuously; Test-WorkflowMirrorCount separately fails if undeclared
        .github/workflows files appear.

        Repos with mirrorWorkflows>0 must have exactly one GitHub CI entrypoint
        at .github/workflows/ci.yml (or ci.yaml), and that entrypoint must invoke:
          - run-plugin-lint-gates.ps1
          - repin-common-submodule.sh --verify-only
          - gitleaks detect
          - scripts/verify-local.ps1

        This does not require byte-identical Gitea/GitHub workflow YAML; it
        requires the behavioral contract to converge on the same Common-owned
        scripts and merge gates.

    .OUTPUTS
        PSCustomObject { Ok, Reason }
    #>
    param(
        [string]$PluginDir,
        [int]$Expected
    )

    if ($Expected -le 0) {
        return [PSCustomObject]@{ Ok = $true; Reason = $null }
    }

    $wfDir = Join-Path $PluginDir '.github/workflows'
    $ciCandidates = @(@(
        Join-Path $wfDir 'ci.yml'
        Join-Path $wfDir 'ci.yaml'
    ) | Where-Object { Test-Path -LiteralPath $_ })

    if ($ciCandidates.Count -eq 0) {
        return [PSCustomObject]@{
            Ok     = $false
            Reason = 'mirrorWorkflows > 0 but .github/workflows/ci.yml (or ci.yaml) is missing'
        }
    }

    if ($ciCandidates.Count -gt 1) {
        return [PSCustomObject]@{
            Ok     = $false
            Reason = 'both .github/workflows/ci.yml and ci.yaml exist; keep a single GitHub CI entrypoint'
        }
    }

    $content = [System.IO.File]::ReadAllText($ciCandidates[0])
    $missing = [System.Collections.Generic.List[string]]::new()

    if ($content -notmatch 'run-plugin-lint-gates\.ps1') {
        $missing.Add('run-plugin-lint-gates.ps1')
    }
    if ($content -notmatch 'repin-common-submodule\.sh\s+--verify-only') {
        $missing.Add('repin-common-submodule.sh --verify-only')
    }
    if ($content -notmatch 'gitleaks\s+detect') {
        $missing.Add('gitleaks detect')
    }
    if ($content -notmatch 'scripts[/\\]verify-local\.ps1') {
        $missing.Add('scripts/verify-local.ps1')
    }

    if ($missing.Count -gt 0) {
        return [PSCustomObject]@{
            Ok     = $false
            Reason = ".github/workflows/ci.yml is missing required gate(s): $($missing -join ', ')"
        }
    }

    return [PSCustomObject]@{ Ok = $true; Reason = $null }
}

function Test-CommonPinAgreement {
    <#
    .SYNOPSIS
        Check whether a set of Common SHAs (from passing plugins) all agree.
    .OUTPUTS
        PSCustomObject { Diverged, UniqueShas }
    #>
    param(
        [string[]]$Shas
    )

    $distinct = @($Shas | Where-Object { $_ } | Sort-Object -Unique)
    $diverged = $distinct.Count -gt 1

    return [PSCustomObject]@{
        Diverged    = $diverged
        UniqueShas  = $distinct
    }
}

function Test-StatusContextRequirementSatisfied {
    <#
    .SYNOPSIS
        Assert a required status context is covered by the branch-protection context list.

    .DESCRIPTION
        Gitea branch protection supports wildcard contexts such as `CI / lint*` so one
        rule can cover both `CI / lint (push)` and `CI / lint (pull_request)`. The
        ecosystem policy names base contexts (`CI / lint`, `CI / verify`, etc.); this
        helper treats exact matches and configured wildcard matches as satisfying the
        requirement, while a concrete event-specific context without a wildcard does
        not satisfy the base requirement.

    .OUTPUTS
        Boolean
    #>
    param(
        [string]$RequiredContext,
        [string[]]$ConfiguredContexts
    )

    foreach ($context in @($ConfiguredContexts)) {
        if ([string]::IsNullOrWhiteSpace($context)) { continue }

        if ($context -eq $RequiredContext) {
            return $true
        }

        if ($context.Contains('*') -and ($RequiredContext -like $context)) {
            return $true
        }
    }

    return $false
}

function Get-BranchProtectionTargets {
    <#
    .SYNOPSIS
        Build the repo/context set checked by -CheckBranchProtection.

    .DESCRIPTION
        The manifest enumerates downstream plugin repos because most ecosystem checks
        need plugin working trees. Branch protection also needs to verify Common itself,
        so this helper appends Lidarr.Plugin.Common with its Common-specific contexts
        unless the manifest already contains it.

    .OUTPUTS
        PSCustomObject { Owner, Repo, RequiredContexts }
    #>
    param(
        [object[]]$Plugins
    )

    $pluginContexts = @('CI / lint', 'CI / verify')
    $commonContexts = @('CI / lint', 'CI / build-test', 'CI / secret-scan')

    $targets = [System.Collections.Generic.List[PSCustomObject]]::new()

    foreach ($plugin in @($Plugins)) {
        $repo = [string]$plugin.giteaRepo
        $isCommon = $repo -eq 'Lidarr.Plugin.Common'
        $targets.Add([PSCustomObject]@{
            Owner            = [string]$plugin.giteaOwner
            Repo             = $repo
            RequiredContexts = if ($isCommon) { $commonContexts } else { $pluginContexts }
        }) | Out-Null
    }

    $hasCommon = @($targets | Where-Object { $_.Repo -eq 'Lidarr.Plugin.Common' }).Count -gt 0
    if (-not $hasCommon) {
        $targets.Add([PSCustomObject]@{
            Owner            = 'RicherTunes'
            Repo             = 'Lidarr.Plugin.Common'
            RequiredContexts = $commonContexts
        }) | Out-Null
    }

    return @($targets)
}

function Test-WorkflowFileValid {
    <#
    .SYNOPSIS
        Assert a single workflow file is non-corrupt and structurally plausible, with full YAML
        parsing when the powershell-yaml module is available.

    .DESCRIPTION
        Attempts real YAML parsing via ConvertFrom-Yaml (powershell-yaml module)
        when available; falls back to a robust heuristic otherwise.

        The heuristic catches the known corruption class where a broken sed
        interleaves a 40-hex Common SHA between every character of the original
        file, producing 40-60 KB of garbage.  Detection criteria:
          - Valid UTF-8
          - Non-empty, <= 512 KB (any real workflow is well under this)
          - A 40-hex string does NOT appear 5+ times (corruption threshold)
          - Top-level 'on:' trigger and 'jobs:' keys are present (line-start match)

        When powershell-yaml is installed, the parser replaces the heuristic key-presence check with
        a real structural parse. Without that module this intentionally stays a corruption and
        workflow-shape guard, not a complete YAML parser.

    .OUTPUTS
        PSCustomObject { Ok, Reason }
    #>
    param(
        [string]$FilePath
    )

    if (-not (Test-Path -LiteralPath $FilePath)) {
        return [PSCustomObject]@{ Ok = $false; Reason = 'file not found' }
    }

    $bytes = [System.IO.File]::ReadAllBytes($FilePath)

    if ($bytes.Length -eq 0) {
        return [PSCustomObject]@{ Ok = $false; Reason = 'empty file' }
    }

    # 512 KB is far above any real workflow; corrupt interleaved files hit 40-60 KB
    # from a ~1 KB source. Setting the ceiling high avoids false positives on
    # legitimately verbose workflows while still catching gross corruption.
    if ($bytes.Length -gt 524288) {
        return [PSCustomObject]@{
            Ok     = $false
            Reason = "file too large ($($bytes.Length) bytes; > 512 KB — corrupt workflow suspected)"
        }
    }

    # Valid UTF-8 (strict; reject bytes that are invalid in UTF-8)
    $content = $null
    try {
        $strictUtf8 = [System.Text.Encoding]::GetEncoding(
            'utf-8',
            [System.Text.EncoderFallback]::ExceptionFallback,
            [System.Text.DecoderFallback]::ExceptionFallback
        )
        $content = $strictUtf8.GetString($bytes)
    }
    catch {
        return [PSCustomObject]@{ Ok = $false; Reason = 'not valid UTF-8' }
    }

    # Corruption guard: the per-character-SHA-interleaving bug inserts the full
    # 40-hex SHA between every character of the original file, so a ~1 KB source
    # produces ~100 SHA occurrences.  Five or more occurrences is already far beyond
    # any legitimate workflow (git SHAs in comments/pins appear 1-3 times at most).
    $shaHits = ([regex]::Matches($content, '[0-9a-f]{40}')).Count
    if ($shaHits -ge 5) {
        return [PSCustomObject]@{
            Ok     = $false
            Reason = "corruption detected: 40-hex pattern appears $shaHits times (per-character SHA interleaving suspected)"
        }
    }

    # Try real YAML parser when powershell-yaml is available
    $psYamlModule = Get-Module -Name 'powershell-yaml' -ListAvailable -ErrorAction SilentlyContinue
    if ($psYamlModule) {
        try {
            Import-Module 'powershell-yaml' -ErrorAction Stop
            $parsed = $content | ConvertFrom-Yaml -ErrorAction Stop
            if ($null -eq $parsed) {
                return [PSCustomObject]@{ Ok = $false; Reason = 'YAML parse returned null (empty document)' }
            }
            if ($parsed -is [System.Collections.IDictionary]) {
                # YAML 1.1: bare 'on' is parsed as boolean $true by most parsers
                $hasJobs = $parsed.Contains('jobs')
                $hasOn   = $parsed.Contains('on') -or $parsed.Contains($true) -or $parsed.Contains('true')
                if (-not $hasJobs) {
                    return [PSCustomObject]@{ Ok = $false; Reason = "missing top-level 'jobs' key" }
                }
                if (-not $hasOn) {
                    return [PSCustomObject]@{ Ok = $false; Reason = "missing top-level 'on' trigger key" }
                }
            }
            return [PSCustomObject]@{ Ok = $true; Reason = $null }
        }
        catch {
            return [PSCustomObject]@{ Ok = $false; Reason = "YAML parse failed: $_" }
        }
    }

    # Heuristic key-presence check (no real parser available)
    # 'on:' may be written bare or quoted; match at line start to avoid false positives.
    $hasOn   = $content -match '(?m)^on\s*:' -or
               $content -match "(?m)^'on'\s*:" -or
               $content -match '(?m)^"on"\s*:'
    $hasJobs = $content -match '(?m)^jobs\s*:'

    if (-not $hasOn) {
        return [PSCustomObject]@{ Ok = $false; Reason = "missing top-level 'on:' trigger key (heuristic)" }
    }
    if (-not $hasJobs) {
        return [PSCustomObject]@{ Ok = $false; Reason = "missing top-level 'jobs:' key (heuristic)" }
    }

    return [PSCustomObject]@{ Ok = $true; Reason = $null }
}

function Test-WorkflowFilesValid {
    <#
    .SYNOPSIS
        Assert ALL .github/workflows and .gitea/workflows yml/yaml files in a plugin repo are
        non-corrupt and structurally plausible, with full YAML parsing when powershell-yaml is
        available.
    .OUTPUTS
        PSCustomObject { Ok, BadFiles, Reason }
    #>
    param(
        [string]$PluginDir,
        [string]$PluginName
    )

    $badFiles = [System.Collections.Generic.List[string]]::new()

    foreach ($subdir in @('.github/workflows', '.gitea/workflows')) {
        $wfDir = Join-Path $PluginDir $subdir
        if (-not (Test-Path -LiteralPath $wfDir)) { continue }

        $files = @(Get-ChildItem -LiteralPath $wfDir -Filter '*.yml'  -File -ErrorAction SilentlyContinue) +
                 @(Get-ChildItem -LiteralPath $wfDir -Filter '*.yaml' -File -ErrorAction SilentlyContinue)

        foreach ($file in $files) {
            $relPath = $file.FullName.Substring($PluginDir.TrimEnd('\', '/').Length).TrimStart([char]'\', [char]'/')
            $check   = Test-WorkflowFileValid -FilePath $file.FullName
            if (-not $check.Ok) {
                $badFiles.Add("$relPath : $($check.Reason)")
            }
        }
    }

    if ($badFiles.Count -gt 0) {
        $detail = ($badFiles | ForEach-Object { "`n  $_" }) -join ''
        return [PSCustomObject]@{
            Ok       = $false
            BadFiles = @($badFiles)
            Reason   = "$($badFiles.Count) invalid workflow file(s):$detail"
        }
    }

    return [PSCustomObject]@{ Ok = $true; BadFiles = @(); Reason = $null }
}

# ============================================================
# Early return when invoked for unit-test dot-sourcing
# ============================================================

if ($DefineFunctionsOnly) { return }

# ============================================================
# Resolve manifest and ecosystem root
# ============================================================

$resolvedManifestPath = if ($ManifestPath) {
    $ManifestPath
}
else {
    Join-Path $PSScriptRoot 'ecosystem-repos.json'
}

if (-not (Test-Path -LiteralPath $resolvedManifestPath)) {
    Write-Host "ERROR: Manifest not found: $resolvedManifestPath" -ForegroundColor Red
    exit 1
}

$manifest = Get-Content -LiteralPath $resolvedManifestPath -Raw | ConvertFrom-Json
$plugins  = $manifest.plugins

# Ecosystem root: default = one level above Common (sibling layout)
$resolvedEcosystemRoot = if ($EcosystemRoot) {
    $EcosystemRoot
}
else {
    # This script is at scripts/ci/ inside the Common repo.
    # $PSScriptRoot/../../.. = C:\R\Alex\github\ when Common = C:\R\Alex\github\lidarr.plugin.common\
    (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
}

if (-not (Test-Path -LiteralPath $resolvedEcosystemRoot)) {
    Write-Host "ERROR: EcosystemRoot not found: $resolvedEcosystemRoot" -ForegroundColor Red
    exit 1
}

# ============================================================
# Run assertions per plugin
# ============================================================

if (-not $CI) {
    Write-Host ''
    Write-Host 'Ecosystem CI-Contract Verifier' -ForegroundColor Cyan
    Write-Host "EcosystemRoot: $resolvedEcosystemRoot" -ForegroundColor White
    Write-Host "Manifest:      $resolvedManifestPath"  -ForegroundColor White
    Write-Host ''
}

$results     = [System.Collections.Generic.List[PSCustomObject]]::new()
$passingShas = [System.Collections.Generic.List[string]]::new()
$anyFail     = $false

foreach ($plugin in $plugins) {
    $repoDir    = $plugin.repoDir
    $pluginDir  = Join-Path $resolvedEcosystemRoot $repoDir
    $expected   = [int]$plugin.mirrorWorkflows

    # In CI, missing manifest repos are a hard failure: a partial checkout cannot
    # prove ecosystem-wide pin/ALC/package convergence.
    if (-not (Test-Path -LiteralPath $pluginDir)) {
        $missingReason = "directory not found at $pluginDir"
        if (-not $CI) {
            Write-Host "  [SKIP] $repoDir — directory not found at $pluginDir" -ForegroundColor DarkYellow
            continue
        }
        else {
            Write-Host "$repoDir FAIL" -ForegroundColor Red
            Write-Host "  repo: $missingReason" -ForegroundColor DarkGray
            $script:anyFail = $true
            $results.Add([PSCustomObject]@{
                RepoDir      = $repoDir
                Ok           = $false
                PinOk        = $false
                PinSha       = $null
                PinReason    = $missingReason
                DocOk        = $false
                DocReason    = $missingReason
                LintOk       = $false
                LintReason   = $missingReason
                VerifyOk     = $false
                VerifyReason = $missingReason
                WfOk         = $false
                WfCount      = 0
                WfReason     = $missingReason
                GhMirrorOk   = $false
                GhMirrorReason = $missingReason
                WfvOk        = $false
                WfvBadFiles  = @()
                WfvReason    = $missingReason
            })
            continue
        }
    }

    $pinResult  = Test-CommonPinIntegrity  -PluginDir $pluginDir -PluginName $repoDir
    $docResult  = Test-DocRefsLintWired           -PluginDir $pluginDir -PluginName $repoDir
    $lintResult = Test-SharedPluginLintRunnerWired -PluginDir $pluginDir -PluginName $repoDir
    $verifyResult = Test-PluginVerifyJobWired     -PluginDir $pluginDir -PluginName $repoDir
    $wfResult   = Test-WorkflowMirrorCount        -PluginDir $pluginDir -Expected $expected
    $ghMirrorResult = Test-GitHubCiMirrorContract -PluginDir $pluginDir -Expected $expected
    $wfvResult  = Test-WorkflowFilesValid         -PluginDir $pluginDir -PluginName $repoDir

    $allOk = $pinResult.Ok -and $docResult.Ok -and $lintResult.Ok -and $verifyResult.Ok -and $wfResult.Ok -and $ghMirrorResult.Ok -and $wfvResult.Ok
    if (-not $allOk) { $script:anyFail = $true }
    if ($pinResult.Ok -and $pinResult.Sha) { $passingShas.Add($pinResult.Sha) | Out-Null }

    $statusLabel = if ($allOk) { 'PASS' } else { 'FAIL' }
    $statusColor = if ($allOk) { 'Green' } else { 'Red' }

    if ($CI) {
        Write-Host "$repoDir $statusLabel" -ForegroundColor $statusColor
        if (-not $pinResult.Ok)  { Write-Host "  pin: $($pinResult.Reason)"    -ForegroundColor DarkGray }
        if (-not $docResult.Ok)  { Write-Host "  doc: $($docResult.Reason)"    -ForegroundColor DarkGray }
        if (-not $lintResult.Ok) { Write-Host "  lint: $($lintResult.Reason)"  -ForegroundColor DarkGray }
        if (-not $verifyResult.Ok) { Write-Host "  verify: $($verifyResult.Reason)" -ForegroundColor DarkGray }
        if (-not $wfResult.Ok)   { Write-Host "  wf:  $($wfResult.Reason)"     -ForegroundColor DarkGray }
        if (-not $ghMirrorResult.Ok) { Write-Host "  gh:  $($ghMirrorResult.Reason)" -ForegroundColor DarkGray }
        if (-not $wfvResult.Ok)  { Write-Host "  wfv: $($wfvResult.Reason)"    -ForegroundColor DarkGray }
    }
    else {
        Write-Host "  [$statusLabel] $repoDir" -ForegroundColor $statusColor

        # Verbose: pin
        if ($pinResult.Ok) {
            Write-Host "         pin : OK ($($pinResult.Sha.Substring(0,12))...)" -ForegroundColor DarkGreen
        }
        else {
            Write-Host "         pin : FAIL — $($pinResult.Reason)" -ForegroundColor Red
        }

        # Verbose: doc-refs
        if ($docResult.Ok) {
            Write-Host "         doc : OK"  -ForegroundColor DarkGreen
        }
        else {
            Write-Host "         doc : FAIL — $($docResult.Reason)" -ForegroundColor Red
        }

        # Verbose: shared lint runner
        if ($lintResult.Ok) {
            Write-Host "         lint: OK (shared runner)" -ForegroundColor DarkGreen
        }
        else {
            Write-Host "         lint: FAIL — $($lintResult.Reason)" -ForegroundColor Red
        }

        # Verbose: verify-local
        if ($verifyResult.Ok) {
            Write-Host "         verify: OK (verify-local)" -ForegroundColor DarkGreen
        }
        else {
            Write-Host "         verify: FAIL — $($verifyResult.Reason)" -ForegroundColor Red
        }

        # Verbose: workflow count
        if ($wfResult.Ok) {
            Write-Host "         wf  : OK ($($wfResult.ActualCount) mirror(s))" -ForegroundColor DarkGreen
        }
        else {
            Write-Host "         wf  : FAIL — $($wfResult.Reason)" -ForegroundColor Red
        }

        # Verbose: GitHub mirror CI parity
        if ($ghMirrorResult.Ok) {
            Write-Host "         gh  : OK (mirror CI contract)" -ForegroundColor DarkGreen
        }
        else {
            Write-Host "         gh  : FAIL — $($ghMirrorResult.Reason)" -ForegroundColor Red
        }

        # Verbose: workflow content validity (F2)
        if ($wfvResult.Ok) {
            Write-Host "         wfv : OK (all workflow files valid)" -ForegroundColor DarkGreen
        }
        else {
            Write-Host "         wfv : FAIL — $($wfvResult.Reason)" -ForegroundColor Red
        }
    }

    $results.Add([PSCustomObject]@{
        RepoDir    = $repoDir
        Ok         = $allOk
        PinOk      = $pinResult.Ok
        PinSha     = $pinResult.Sha
        PinReason  = $pinResult.Reason
        DocOk      = $docResult.Ok
        DocReason  = $docResult.Reason
        LintOk     = $lintResult.Ok
        LintReason = $lintResult.Reason
        VerifyOk   = $verifyResult.Ok
        VerifyReason = $verifyResult.Reason
        WfOk       = $wfResult.Ok
        WfCount    = $wfResult.ActualCount
        WfReason   = $wfResult.Reason
        GhMirrorOk = $ghMirrorResult.Ok
        GhMirrorReason = $ghMirrorResult.Reason
        WfvOk      = $wfvResult.Ok
        WfvBadFiles= $wfvResult.BadFiles
        WfvReason  = $wfvResult.Reason
    })
}

# ============================================================
# Cross-plugin SHA agreement (fail by default)
# ============================================================

$agrResult = Test-CommonPinAgreement -Shas @($passingShas)
if ($agrResult.Diverged) {
    Write-Host ''
    Write-Host 'FAIL: Passing plugins pin different Common SHAs:' -ForegroundColor Red
    foreach ($sha in $agrResult.UniqueShas) {
        $owners = @($results | Where-Object { $_.PinSha -eq $sha } | Select-Object -ExpandProperty RepoDir)
        Write-Host "  $sha  ($($owners -join ', '))" -ForegroundColor Red
    }
    Write-Host 'All active plugins must be re-pinned before the ecosystem ALC/package proof is considered valid.' -ForegroundColor Red
    $anyFail = $true
}

# ============================================================
# Optional branch-protection check
# ============================================================

if ($CheckBranchProtection) {
    Write-Host ''
    Write-Host 'Branch-protection check (Gitea API)...' -ForegroundColor Cyan

    # Retrieve Gitea token without printing it
    $credInput = "protocol=http`nhost=192.168.2.59:3001`n`n"
    $token     = ($credInput | git credential fill 2>$null | Select-String '^password=') -replace '^password=', ''

    if (-not $token) {
        Write-Host '  WARNING: No Gitea token available; skipping branch-protection check.' -ForegroundColor Yellow
        Write-Host '  (Provide credentials for 192.168.2.59:3001 via git credential fill.)' -ForegroundColor DarkGray
    }
    else {
        $giteaBase     = 'http://192.168.2.59:3001/api/v1'
        $bpFail        = $false

        $branchProtectionTargets = @(Get-BranchProtectionTargets -Plugins $plugins)

        foreach ($target in $branchProtectionTargets) {
            $owner   = $target.Owner
            $repo    = $target.Repo
            $headers = @{ Authorization = "token $token" }

            try {
                $url = "$giteaBase/repos/$owner/$repo/branch_protections"
                $bps = Invoke-RestMethod -Uri $url -Headers $headers -Method Get -ErrorAction Stop
            }
            catch {
                Write-Host "  WARNING: Could not query branch protections for ${owner}/${repo}: $_" -ForegroundColor Yellow
                continue
            }

            $mainBp = $bps | Where-Object { $_.branch_name -eq 'main' } | Select-Object -First 1
            if (-not $mainBp) {
                Write-Host "  [WARN] ${repo}: no branch protection found for 'main'" -ForegroundColor Yellow
                continue
            }

            $required    = @($target.RequiredContexts)
            $hasEnabled  = $mainBp.enable_status_check -eq $true
            $actual      = @($mainBp.status_check_contexts)
            $missing     = @($required | Where-Object {
                -not (Test-StatusContextRequirementSatisfied -RequiredContext $_ -ConfiguredContexts $actual)
            })

            if (-not $hasEnabled -or $missing.Count -gt 0) {
                $bpFail = $true
                Write-Host "  [FAIL] ${repo} branch protection:" -ForegroundColor Red
                if (-not $hasEnabled) {
                    Write-Host '         enable_status_check=false' -ForegroundColor Red
                }
                foreach ($ctx in $missing) {
                    Write-Host "         Missing required context: $ctx" -ForegroundColor Red
                }
            }
            else {
                Write-Host "  [PASS] ${repo} branch protection OK" -ForegroundColor Green
            }
        }

        if ($bpFail) {
            $script:anyFail = $true
        }
    }
}

# ============================================================
# Summary table and exit
# ============================================================

Write-Host ''
$passCount = @($results | Where-Object { $_.Ok }).Count
$failCount = @($results | Where-Object { -not $_.Ok }).Count
$totalMsg  = "$passCount/$($results.Count) plugins PASS"

if ($anyFail) {
    Write-Host "[FAIL] $totalMsg" -ForegroundColor Red
    exit 1
}
else {
    Write-Host "[OK] $totalMsg" -ForegroundColor Green
    exit 0
}
