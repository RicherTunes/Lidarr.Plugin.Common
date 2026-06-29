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

      3. Workflow-mirror declaration
         Count of .github/workflows/*.yml (+.yaml) files matches the manifest's
         mirrorWorkflows field.

    Cross-plugin SHA agreement is also evaluated: if passing plugins pin different
    Common SHAs, a non-failing warning is emitted (re-pins may be mid-flight).

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

    # Validate format: exactly 40 lowercase hex + LF (41 bytes)
    $rawBytes = [System.IO.File]::ReadAllBytes($shaFile)
    if ($rawBytes.Length -ne 41) {
        return [PSCustomObject]@{
            Ok     = $false
            Sha    = $null
            Reason = "ext-common-sha.txt wrong size ($($rawBytes.Length) bytes, expected 41 = 40 hex + LF)"
        }
    }
    if ($rawBytes[40] -ne 0x0A) {
        return [PSCustomObject]@{
            Ok     = $false
            Sha    = $null
            Reason = "ext-common-sha.txt must end with LF (0x0A), got 0x$($rawBytes[40].ToString('X2'))"
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

    # Skip missing dirs (warn, don't fail)
    if (-not (Test-Path -LiteralPath $pluginDir)) {
        if (-not $CI) {
            Write-Host "  [SKIP] $repoDir — directory not found at $pluginDir" -ForegroundColor DarkYellow
        }
        else {
            Write-Host "SKIP $repoDir (not present)" -ForegroundColor DarkYellow
        }
        continue
    }

    $pinResult  = Test-CommonPinIntegrity  -PluginDir $pluginDir -PluginName $repoDir
    $docResult  = Test-DocRefsLintWired           -PluginDir $pluginDir -PluginName $repoDir
    $lintResult = Test-SharedPluginLintRunnerWired -PluginDir $pluginDir -PluginName $repoDir
    $wfResult   = Test-WorkflowMirrorCount        -PluginDir $pluginDir -Expected $expected
    $wfvResult  = Test-WorkflowFilesValid         -PluginDir $pluginDir -PluginName $repoDir

    $allOk = $pinResult.Ok -and $docResult.Ok -and $lintResult.Ok -and $wfResult.Ok -and $wfvResult.Ok
    if (-not $allOk) { $script:anyFail = $true }
    if ($pinResult.Ok -and $pinResult.Sha) { $passingShas.Add($pinResult.Sha) | Out-Null }

    $statusLabel = if ($allOk) { 'PASS' } else { 'FAIL' }
    $statusColor = if ($allOk) { 'Green' } else { 'Red' }

    if ($CI) {
        Write-Host "$repoDir $statusLabel" -ForegroundColor $statusColor
        if (-not $pinResult.Ok)  { Write-Host "  pin: $($pinResult.Reason)"    -ForegroundColor DarkGray }
        if (-not $docResult.Ok)  { Write-Host "  doc: $($docResult.Reason)"    -ForegroundColor DarkGray }
        if (-not $lintResult.Ok) { Write-Host "  lint: $($lintResult.Reason)"  -ForegroundColor DarkGray }
        if (-not $wfResult.Ok)   { Write-Host "  wf:  $($wfResult.Reason)"     -ForegroundColor DarkGray }
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

        # Verbose: workflow count
        if ($wfResult.Ok) {
            Write-Host "         wf  : OK ($($wfResult.ActualCount) mirror(s))" -ForegroundColor DarkGreen
        }
        else {
            Write-Host "         wf  : FAIL — $($wfResult.Reason)" -ForegroundColor Red
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
        WfOk       = $wfResult.Ok
        WfCount    = $wfResult.ActualCount
        WfReason   = $wfResult.Reason
        WfvOk      = $wfvResult.Ok
        WfvBadFiles= $wfvResult.BadFiles
        WfvReason  = $wfvResult.Reason
    })
}

# ============================================================
# Cross-plugin SHA agreement (warn, never fail)
# ============================================================

$agrResult = Test-CommonPinAgreement -Shas @($passingShas)
if ($agrResult.Diverged) {
    Write-Host ''
    Write-Host 'WARNING: Passing plugins pin different Common SHAs (re-pin may be mid-flight):' -ForegroundColor Yellow
    foreach ($sha in $agrResult.UniqueShas) {
        $owners = @($results | Where-Object { $_.PinSha -eq $sha } | Select-Object -ExpandProperty RepoDir)
        Write-Host "  $sha  ($($owners -join ', '))" -ForegroundColor Yellow
    }
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

        # Expected required contexts per repo type
        $pluginContexts = @('CI / lint', 'CI / verify')
        $commonContexts = @('CI / lint', 'CI / build-test', 'CI / secret-scan')

        foreach ($plugin in $plugins) {
            $owner   = $plugin.giteaOwner
            $repo    = $plugin.giteaRepo
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

            $isPlugin    = $repo -ne 'Lidarr.Plugin.Common'
            $required    = if ($isPlugin) { $pluginContexts } else { $commonContexts }
            $hasEnabled  = $mainBp.enable_status_check -eq $true
            $actual      = @($mainBp.status_check_contexts)
            $missing     = @($required | Where-Object { $_ -notin $actual })

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
