#Requires -Modules Pester

<#
.SYNOPSIS
    TDD gate: build/PluginPackaging.targets must contain a ValidatePackageClosure
    target that actively rejects forbidden DLLs listed in parity-spec.json.

.DESCRIPTION
    Phase 3 — Task B enforcement gate.

    Previous behaviour: PluginPackaging.targets cleaned up forbidden DLLs *silently*
    via Delete tasks inside RepackPlugin but never *rejected* a build that shipped
    them.  brainarr and applemusicarr were able to produce non-compliant packages
    without any build failure.

    This test suite validates two things:

    1. STATIC CHECK — build/PluginPackaging.targets contains a Target named
       ValidatePackageClosure with the expected structural elements:
         - AfterTargets="InjectPluginBuildMetadata" wiring
         - References to parity-spec.json (the forbidden list source)
         - An <Error> task for the failure case
         - A reference to docs/MULTI_PLUGIN_ALC_VALIDATION.md in the error message

    2. BEHAVIORAL CHECK — ValidatePackageClosure returns a non-zero exit code
       (via msbuild /t:ValidatePackageClosure) when a forbidden DLL is present in
       the simulated output directory.

    The behavioral check requires msbuild or dotnet msbuild to be on PATH and will
    SKIP if neither is available.
#>

BeforeAll {
    $script:RepoRoot    = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:TargetsFile = Join-Path $script:RepoRoot 'build\PluginPackaging.targets'
    $script:ParitySpec  = Join-Path $script:RepoRoot 'scripts\parity-spec.json'
}

Describe 'build/PluginPackaging.targets — ValidatePackageClosure gate' {

    # ─────────────────────────────────────────────────────────────────────────
    # Static structural checks (always run, no build tooling required)
    # ─────────────────────────────────────────────────────────────────────────

    Context 'static structure' {

        BeforeAll {
            if (Test-Path $script:TargetsFile) {
                $script:TargetsContent = Get-Content $script:TargetsFile -Raw
            } else {
                $script:TargetsContent = $null
            }
        }

        It 'build/PluginPackaging.targets exists' {
            Test-Path $script:TargetsFile | Should -BeTrue
        }

        It 'contains a Target named ValidatePackageClosure' {
            $script:TargetsContent | Should -Match 'Target\s[^>]*Name="ValidatePackageClosure"' `
                -Because 'Phase 3 requires an actively-enforcing gate target (not just silent cleanup)'
        }

        It 'ValidatePackageClosure runs AfterTargets InjectPluginBuildMetadata' {
            $script:TargetsContent | Should -Match 'AfterTargets="InjectPluginBuildMetadata"' `
                -Because 'the gate must run after the build and metadata injection so the output dir is fully populated'
        }

        It 'ValidatePackageClosure references parity-spec.json as the forbidden list source' {
            $script:TargetsContent | Should -Match 'parity-spec\.json' `
                -Because 'the gate must read forbiddenPackageContents from parity-spec.json (single source of truth)'
        }

        It 'ValidatePackageClosure contains an <Error> task for the failure case' {
            $script:TargetsContent | Should -Match '<Error\b' `
                -Because 'the gate must emit a hard MSBuild Error (not just a Warning) when forbidden DLLs are found'
        }

        It 'ValidatePackageClosure error message references docs/MULTI_PLUGIN_ALC_VALIDATION.md' {
            $script:TargetsContent | Should -Match 'MULTI_PLUGIN_ALC_VALIDATION\.md' `
                -Because 'the error must guide developers to the explanation and fix instructions'
        }

        It 'ValidatePackageClosure error message mentions PrivateAssets or ExcludeAssets fix instructions' {
            $script:TargetsContent | Should -Match 'PrivateAssets|ExcludeAssets' `
                -Because 'the error must tell developers HOW to fix the issue'
        }

        It 'ValidatePackageClosure is guarded by PluginPackagingDisable condition' {
            # The gate should respect the opt-out escape hatch used in CI skip scenarios
            $script:TargetsContent | Should -Match "PluginPackagingDisable.*!=.*'true'|'true'.*!=.*PluginPackagingDisable" `
                -Because 'like RepackPlugin, the gate must be skippable via PluginPackagingDisable=true'
        }

        It 'parity-spec.json forbiddenPackageContents is non-empty' {
            if (-not (Test-Path $script:ParitySpec)) {
                Set-ItResult -Skipped -Because 'parity-spec.json not found'
                return
            }
            $spec = Get-Content $script:ParitySpec -Raw | ConvertFrom-Json
            $spec.versionContract.forbiddenPackageContents.Count | Should -BeGreaterThan 0 `
                -Because 'the spec must declare at least one forbidden DLL for the gate to enforce'
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # Behavioral check via MSBuild (skipped if tooling unavailable)
    # ─────────────────────────────────────────────────────────────────────────

    Context 'behavioral: rejects forbidden DLL via msbuild' {

        BeforeAll {
            # Detect if msbuild / dotnet msbuild is available
            $script:MsBuildCmd = $null
            if (Get-Command 'dotnet' -ErrorAction SilentlyContinue) {
                $script:MsBuildCmd = 'dotnet msbuild'
            } elseif (Get-Command 'msbuild' -ErrorAction SilentlyContinue) {
                $script:MsBuildCmd = 'msbuild'
            }
        }

        It 'ValidatePackageClosure fails the build when a forbidden DLL is present in output dir' {
            if ($null -eq $script:MsBuildCmd) {
                Set-ItResult -Skipped -Because 'dotnet / msbuild not found on PATH; skipping behavioral check'
                return
            }
            if (-not (Test-Path $script:ParitySpec)) {
                Set-ItResult -Skipped -Because 'parity-spec.json not found; skipping behavioral check'
                return
            }

            # Pick the first forbidden DLL from the spec as our test case
            $spec         = Get-Content $script:ParitySpec -Raw | ConvertFrom-Json
            $forbiddenDll = $spec.versionContract.forbiddenPackageContents[0]

            # Create a temp directory to act as a simulated plugin output directory
            $tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) "pester-vpc-test-$([System.Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null

            try {
                # Stage the forbidden DLL (empty file is sufficient for name-based detection)
                $null = New-Item -ItemType File -Path (Join-Path $tmpDir $forbiddenDll) -Force

                # Write a minimal .proj that imports PluginPackaging.targets and sets up properties
                $projContent = @"
<Project>
  <PropertyGroup>
    <TargetDir>$($tmpDir.Replace('\','/'))/</TargetDir>
    <AssemblyName>Test.Plugin</AssemblyName>
    <PluginAssemblyFileName>Test.Plugin.dll</PluginAssemblyFileName>
    <PluginManifestFileName>plugin.json</PluginManifestFileName>
    <!-- Enable ValidatePackageClosure but disable the rest to avoid ILRepack -->
    <PluginPackagingDisable>false</PluginPackagingDisable>
  </PropertyGroup>
  <Import Project="$($script:TargetsFile.Replace('\','/'))" />
</Project>
"@
                $projFile = Join-Path $tmpDir 'test-validate.proj'
                Set-Content -LiteralPath $projFile -Value $projContent -Encoding UTF8

                # Run ValidatePackageClosure target via dotnet msbuild
                $output = & dotnet msbuild $projFile /t:ValidatePackageClosure /nologo /v:minimal 2>&1
                $exitCode = $LASTEXITCODE

                # The build must fail (non-zero exit) because a forbidden DLL is present
                $exitCode | Should -Not -Be 0 `
                    -Because "ValidatePackageClosure must reject a build containing forbidden DLL '$forbiddenDll'"

                # The output should mention the forbidden DLL or the fix instructions
                $outputStr = $output -join "`n"
                $outputStr | Should -Match 'FORBIDDEN|forbidden|MULTI_PLUGIN_ALC_VALIDATION|ValidatePackageClosure' `
                    -Because 'the error message must clearly identify the violation'
            }
            finally {
                Remove-Item -LiteralPath $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'ValidatePackageClosure passes when no forbidden DLLs are present' {
            if ($null -eq $script:MsBuildCmd) {
                Set-ItResult -Skipped -Because 'dotnet / msbuild not found on PATH; skipping behavioral check'
                return
            }
            if (-not (Test-Path $script:ParitySpec)) {
                Set-ItResult -Skipped -Because 'parity-spec.json not found; skipping behavioral check'
                return
            }

            # Create a temp directory with only an allowed DLL
            $tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) "pester-vpc-clean-$([System.Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null

            try {
                # Stage only the main plugin DLL (not forbidden)
                $null = New-Item -ItemType File -Path (Join-Path $tmpDir 'Test.Plugin.dll') -Force

                $projContent = @"
<Project>
  <PropertyGroup>
    <TargetDir>$($tmpDir.Replace('\','/'))/</TargetDir>
    <AssemblyName>Test.Plugin</AssemblyName>
    <PluginAssemblyFileName>Test.Plugin.dll</PluginAssemblyFileName>
    <PluginManifestFileName>plugin.json</PluginManifestFileName>
    <PluginPackagingDisable>false</PluginPackagingDisable>
  </PropertyGroup>
  <Import Project="$($script:TargetsFile.Replace('\','/'))" />
</Project>
"@
                $projFile = Join-Path $tmpDir 'test-validate-clean.proj'
                Set-Content -LiteralPath $projFile -Value $projContent -Encoding UTF8

                $null = & dotnet msbuild $projFile /t:ValidatePackageClosure /nologo /v:minimal 2>&1
                $exitCode = $LASTEXITCODE

                $exitCode | Should -Be 0 `
                    -Because 'ValidatePackageClosure must not fail when no forbidden DLLs are present'
            }
            finally {
                Remove-Item -LiteralPath $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
