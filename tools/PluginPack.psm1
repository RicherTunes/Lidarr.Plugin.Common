# snippet-skip-compile
# snippet:plugin-pack
function Get-PluginPackPropertyArguments {
    param([string]$Configuration, [string]$Framework, [string[]]$ExtraBuildArgs = @())
    # One property context for both compilation and package identity evaluation.
    @("-p:Configuration=$Configuration", "-p:TargetFramework=$Framework",
        '-p:CopyLocalLockFileAssemblies=true', '-p:ContinuousIntegrationBuild=true') + $ExtraBuildArgs
}

function Get-PluginOutput {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Csproj,

        [string]$Framework = 'net8.0',
        [string]$Configuration = 'Release',
        [string[]]$ExtraBuildArgs = @()
    )

    $projectPath = Resolve-Path -LiteralPath $Csproj
    $projectDirectory = Split-Path -Parent $projectPath
    $publishDirectory = Join-Path $projectDirectory "artifacts/publish/$Framework/$Configuration"
    # Always start from a clean publish folder. `dotnet build -o` does not remove
    # stale files from previous runs, which can accidentally ship old manifests or assets.
    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

    # Use `dotnet build` instead of `dotnet publish` so projects using PluginPackaging.targets
    # (ILRepack) produce the same merged output that is used in real plugin deployment.
    # `-m:1` serializes MSBuild project builds. ILRepack-merged plugin projects reference the same
    # Abstractions/Common projects; parallel builds can run GenerateDepsFile for one project twice at
    # once and collide on `*.deps.json` ("being used by another process"), an intermittent packaging
    # failure. Serializing removes the race at negligible cost for these small project graphs.
    $buildArgs = @($projectPath, '-o', $publishDirectory, '-m:1') +
        @(Get-PluginPackPropertyArguments -Configuration $Configuration -Framework $Framework -ExtraBuildArgs $ExtraBuildArgs)
    # Capture build output so failures are diagnosable. Previously piped to Out-Null,
    # which made CI packaging failures impossible to triage (generic "dotnet build failed"
    # with no compiler/MSBuild error). On failure, replay the captured output to stderr.
    $buildOutput = & dotnet build @buildArgs 2>&1

    if ($LASTEXITCODE -ne 0) {
        $buildOutput | ForEach-Object { Write-Host $_ }
        throw "dotnet build failed for $Csproj ($Framework|$Configuration). See build output above."
    }

    return $publishDirectory
}

function Test-PluginManifest {
    <#
    .SYNOPSIS
    Validates plugin manifest against schema and optionally verifies entrypoints.

    .PARAMETER Csproj
        Path to the .csproj project file.

    .PARAMETER Manifest
        Path to the plugin.json manifest file.

    .PARAMETER AbstractionsPackage
        Name of the Abstractions NuGet package. Default: "Lidarr.Plugin.Abstractions"

    .PARAMETER PublishPath
        Path to the publish output directory (for entrypoint resolution).

    .PARAMETER ResolveEntryPoints
        When specified, verifies entrypoint types exist in the built assembly.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Csproj,

        [Parameter(Mandatory = $true)]
        [string]$Manifest,

        [string]$AbstractionsPackage = 'Lidarr.Plugin.Abstractions',

        [string]$PublishPath,

        [switch]$ResolveEntryPoints
    )

    $scriptRoot = Split-Path -Parent $PSCommandPath
    $manifestScript = Join-Path $scriptRoot 'ManifestCheck.ps1'

    $params = @{
        ProjectPath = $Csproj
        ManifestPath = $Manifest
        AbstractionsPackage = $AbstractionsPackage
    }

    if ($PublishPath) {
        $params['PublishPath'] = $PublishPath
    }
    if ($ResolveEntryPoints) {
        $params['ResolveEntryPoints'] = $true
    }

    # Reset $LASTEXITCODE before calling the manifest script to prevent stale
    # exit codes from previous native commands (e.g., gh release download)
    # from causing false failures.
    $global:LASTEXITCODE = 0
    & $manifestScript @params
    if ($LASTEXITCODE -ne 0) {
        throw "Manifest validation failed for $Manifest."
    }
}

function Get-ManagedAssemblyReferenceNames {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$AssemblyPath
    )

    $resolvedPath = Resolve-Path -LiteralPath $AssemblyPath
    $stream = [IO.File]::OpenRead($resolvedPath)
    $peReader = $null
    try {
        $peReader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        if (-not $peReader.HasMetadata) {
            throw "Assembly '$AssemblyPath' does not contain managed metadata."
        }

        $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)
        $references = @()
        foreach ($handle in $metadata.AssemblyReferences) {
            $reference = $metadata.GetAssemblyReference($handle)
            $references += $metadata.GetString($reference.Name)
        }

        return $references
    }
    finally {
        if ($peReader) { $peReader.Dispose() }
        $stream.Dispose()
    }
}

function Assert-PluginAssemblyHasNoMergedReferences {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$AssemblyPath,

        [string[]]$ForbiddenReferences = @('Lidarr.Plugin.Abstractions', 'Lidarr.Plugin.Common')
    )

    if (-not (Test-Path -LiteralPath $AssemblyPath)) {
        throw "Plugin assembly not found at '$AssemblyPath'."
    }

    $references = @(Get-ManagedAssemblyReferenceNames -AssemblyPath $AssemblyPath)
    $violations = @($references | Where-Object { $ForbiddenReferences -contains $_ } | Select-Object -Unique)
    if ($violations.Count -gt 0) {
        throw "Plugin assembly '$AssemblyPath' still has external merged assembly reference(s): $($violations -join ', '). Rebuild with PluginPackaging.targets/ILRepack internalization before packaging; do not rely on removed sidecars."
    }
}
function Resolve-PluginPackVersion {
    <#
    .SYNOPSIS
        Resolves the package version through MSBuild, in the actual build context.
    .DESCRIPTION
        XML literals are not authoritative: parent conditions, imported targets,
        configuration and command-line properties can override them. AssemblyVersion
        is a different contract. Never fabricate a version when evaluation fails.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$CsprojPath,
        # Retained for compatibility with callers of the previous XML-based API.
        [xml]$ProjectXml,
        [string]$Configuration = 'Release',
        [string]$Framework = 'net8.0',
        [string[]]$ExtraBuildArgs = @()
    )
    $previousNoLogo = $env:DOTNET_NOLOGO
    try {
        $env:DOTNET_NOLOGO = '1'
        $evaluationArgs = @($CsprojPath) +
            @(Get-PluginPackPropertyArguments -Configuration $Configuration -Framework $Framework -ExtraBuildArgs $ExtraBuildArgs) +
            @('-getProperty:Version', '-nologo')
        $msbuildOutput = & dotnet msbuild @evaluationArgs 2>&1
        $evaluationExit = $LASTEXITCODE
        if ($evaluationExit -ne 0) { throw "MSBuild version evaluation failed (exit $evaluationExit) for '$CsprojPath': $msbuildOutput" }
        # Accept only a complete version line, never numbers embedded in banners.
        $version = @($msbuildOutput | ForEach-Object { "$_".Trim() } |
            Where-Object { $_ -match '^\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$' }) | Select-Object -Last 1
        if (-not $version) { throw "MSBuild returned no valid package version for '$CsprojPath'." }
        return $version
    }
    finally {
        $env:DOTNET_NOLOGO = $previousNoLogo
    }
}

function Assert-PluginReferencedHost {
    param(
        [Parameter(Mandatory)][IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Manifest,
        [version]$HostVersion
    )
    $minimum = $null
    if (-not [version]::TryParse([string]$Manifest['minHostVersion'], [ref]$minimum)) {
        throw 'Host requirement: manifest minimum must be a valid host version.'
    }
    $main = [string]$Manifest['main']
    $entries = @($Archive.Entries | Where-Object { $_.FullName -ceq $main })
    if ($main -notmatch '^Lidarr\.Plugin\.[A-Za-z0-9_.-]+\.dll$' -or $entries.Count -ne 1 -or
        $entries[0].Length -gt 256MB) {
        throw 'Host requirement: exactly one bounded main assembly is required.'
    }
    $buffer = [IO.MemoryStream]::new()
    $stream = $entries[0].Open()
    try {
        $block = [byte[]]::new(64KB)
        while (($read = $stream.Read($block, 0, $block.Length)) -gt 0) {
            if ($buffer.Length + $read -gt 256MB) { throw 'Host requirement: main assembly exceeds the metadata inspection limit.' }
            $buffer.Write($block, 0, $read)
        }
        $buffer.Position = 0
        $required = [version]'0.0.0.0'
        try {
            # Metadata-only inspection: never load or execute plugin code, and
            # never resolve its dependencies in the PowerShell process.
            $pe = [System.Reflection.PortableExecutable.PEReader]::new($buffer)
            try {
                $reader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
                if (-not $reader.IsAssembly) { throw 'The PE is not a managed assembly.' }
                foreach ($handle in $reader.AssemblyReferences) {
                    $reference = $reader.GetAssemblyReference($handle)
                    $name = $reader.GetString($reference.Name)
                    if (($name -eq 'Lidarr' -or $name.StartsWith('Lidarr.', [StringComparison]::Ordinal)) -and
                        -not $name.StartsWith('Lidarr.Plugin.', [StringComparison]::Ordinal) -and $reference.Version -gt $required) {
                        $required = $reference.Version
                    }
                }
            }
            finally { $pe.Dispose() }
        }
        catch { throw "Host requirement: unreadable main assembly metadata: $($_.Exception.Message)" }
        if ($minimum -lt $required) {
            throw "Host requirement: declared minimum '$minimum' understates the compiled Lidarr reference '$required'."
        }
        if ($null -ne $HostVersion -and $HostVersion -lt $minimum) {
            throw "Host requirement: selected host '$HostVersion' is older than required '$minimum'."
        }
    }
    finally { $stream.Dispose(); $buffer.Dispose() }
}

function Assert-PluginPackageIdentity {
    <# Validates the artifact's own records without extracting or loading code. #>
    param(
        [Parameter(Mandatory)][string]$ZipPath,
        [string]$ExpectedVersion,
        [switch]$RequireVersionedFileName,
        [switch]$ValidateHostRequirements,
        [version]$HostVersion
    )
    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ZipPath).Path)
    try {
        $documents = @{}
        foreach ($name in @('plugin.json', 'package-metadata.json')) {
            $entries = @($archive.Entries | Where-Object { $_.FullName -ceq $name })
            if ($entries.Count -ne 1 -or $entries[0].Length -gt 1MB) {
                throw "Package identity requires exactly one bounded root '$name'."
            }
            $reader = [IO.StreamReader]::new($entries[0].Open())
            try { $documents[$name] = $reader.ReadToEnd() | ConvertFrom-Json -AsHashtable }
            finally { $reader.Dispose() }
            if ($documents[$name] -isnot [System.Collections.IDictionary]) { throw "Package identity record '$name' must be an object." }
        }
        $manifest = $documents['plugin.json']
        $metadata = $documents['package-metadata.json']
        $id = [string]$manifest['id']
        $version = [string]$manifest['version']
        $framework = [string]$metadata['framework']
        if ($id -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or
            $version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$' -or
            [string]::IsNullOrWhiteSpace($framework)) {
            throw 'Package identity contains an invalid ID, version, or framework.'
        }
        $declaredFrameworks = @($manifest['targetFramework']) + @($manifest['targetFrameworks'])
        if ([string]$metadata['packageId'] -cne $id -or [string]$metadata['version'] -cne $version -or
            $declaredFrameworks -cnotcontains $framework) {
            throw 'Package identity mismatch between plugin.json and package-metadata.json.'
        }
        if ($ExpectedVersion -and $ExpectedVersion -cne $version) {
            throw "Package identity mismatch: manifest '$version' does not match evaluated build '$ExpectedVersion'."
        }
        if ($RequireVersionedFileName -and [IO.Path]::GetFileName($ZipPath) -cne "$id-$version-$framework.zip") {
            throw "Package identity mismatch: filename must be '$id-$version-$framework.zip'."
        }
        if ($ValidateHostRequirements -or $null -ne $HostVersion) {
            Assert-PluginReferencedHost -Archive $archive -Manifest $manifest -HostVersion $HostVersion
        }
    }
    finally { $archive.Dispose() }
}

function New-PluginPackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Csproj,

        [Parameter(Mandatory = $true)]
        [string]$Manifest,

        [string]$Framework = 'net8.0',
        [string]$Configuration = 'Release',

        [switch]$MergeAssemblies,
        [string]$IlRepackRsp = 'tools/ilrepack.rsp',
        [string]$InternalizeExclude = 'tools/internalize.exclude',

        # Canonical Abstractions injection parameters
        [string]$CanonicalAbstractionsVersion,
        [string]$CanonicalAbstractionsSha256,
        [string]$CanonicalAbstractionsPath,
        [switch]$RequireCanonicalAbstractions,

        # Entrypoint validation
        [switch]$ResolveEntryPoints,

        # Optional: keep additional runtime assemblies as separate DLLs (advanced).
        # Most plugins should prefer merging/internalizing via PluginPackaging.targets.
        [string[]]$AdditionalKeepAssemblies = @(),

        # Extra MSBuild arguments passed through to dotnet build (e.g., -p:SkipHostBridge=true).
        [string[]]$ExtraBuildArgs = @()
    )

    $csprojPath = Resolve-Path -LiteralPath $Csproj
    $manifestPath = Resolve-Path -LiteralPath $Manifest
    $publishPath = Get-PluginOutput -Csproj $csprojPath -Framework $Framework -Configuration $Configuration -ExtraBuildArgs $ExtraBuildArgs

    # Basic manifest validation first (before any assembly modifications)
    Test-PluginManifest -Csproj $csprojPath -Manifest $manifestPath

    # Ensure the validated manifest is included in the final package.
    # Some repos generate `plugin.json` outside the publish output (e.g., bin/plugin.json),
    # which would otherwise pass validation but never be shipped.
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $publishPath 'plugin.json') -Force

    # MANDATORY: Inject gitSha and buildTimestamp into plugin.json for artifact freshness validation.
    # - gitSha: Short git commit hash (8 chars) or "unknown" if git unavailable
    # - buildTimestamp: ISO 8601 UTC timestamp of when the package was built
    $packageManifestPath = Join-Path $publishPath 'plugin.json'
    $packageManifest = Get-Content -LiteralPath $packageManifestPath -Raw | ConvertFrom-Json

    # Get git SHA with fallback to "unknown" (CI without git, exported archives)
    $gitSha = 'unknown'
    try {
        $gitOutput = & git rev-parse --short=8 HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and $gitOutput) {
            $gitSha = $gitOutput.Trim()
        }
    } catch {
        # Git not available - use "unknown"
    }
    # Fallback to GITHUB_SHA environment variable (GitHub Actions provides this)
    if ($gitSha -eq 'unknown' -and $env:GITHUB_SHA) {
        $gitSha = $env:GITHUB_SHA.Substring(0, 8)
    }

    # ISO 8601 UTC timestamp
    $buildTimestamp = [DateTime]::UtcNow.ToString('o')

    # Add build metadata to manifest
    $packageManifest | Add-Member -NotePropertyName 'gitSha' -NotePropertyValue $gitSha -Force
    $packageManifest | Add-Member -NotePropertyName 'buildTimestamp' -NotePropertyValue $buildTimestamp -Force
    $packageManifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $packageManifestPath -Encoding UTF8

    Write-Host "Injected build metadata: gitSha=$gitSha, buildTimestamp=$buildTimestamp" -ForegroundColor Cyan

    # Parse project metadata FIRST (before any cleanup/merge operations)  
    $projectXml = [xml](Get-Content -LiteralPath $csprojPath)
    $assemblyName = $projectXml.Project.PropertyGroup.AssemblyName | Select-Object -Last 1
    if (-not $assemblyName) { $assemblyName = [IO.Path]::GetFileNameWithoutExtension($csprojPath) }
    
    # Use exactly the configuration/framework/overrides used to build the DLL.
    $version = Resolve-PluginPackVersion -CsprojPath $csprojPath -Configuration $Configuration -Framework $Framework -ExtraBuildArgs $ExtraBuildArgs

    # Step 1: Merge assemblies (if requested) - BEFORE cleanup so deps exist
    if ($MergeAssemblies.IsPresent) {
        Invoke-PluginMerge -PublishPath $publishPath -AssemblyName $assemblyName -IlRepackRsp $IlRepackRsp -InternalizeExclude $InternalizeExclude
    }

    # Step 2: Clean up publish output AFTER merge - removes extra deps, keeps runtime deps
    # This matches PluginPackaging.targets behavior
    Invoke-PluginCleanup -PublishPath $publishPath -AssemblyName $assemblyName -AdditionalKeep $AdditionalKeepAssemblies

    $mainAssemblyPath = Join-Path $publishPath "$assemblyName.dll"
    Assert-PluginAssemblyHasNoMergedReferences -AssemblyPath $mainAssemblyPath

    # Step 3: Historical canonical Abstractions sidecar injection is intentionally
    # disabled. Abstractions is now merged/internalized into each plugin DLL by
    # PluginPackaging.targets; shipping it as a sidecar reintroduces the cross-ALC
    # conflict that the merged-DLL packaging policy fixed.
    $doCanonicalInjection = $CanonicalAbstractionsVersion -or $CanonicalAbstractionsPath -or $RequireCanonicalAbstractions

    $canonicalResult = $null
    if ($doCanonicalInjection) {
        Write-Warning "Canonical Abstractions sidecar injection is deprecated and skipped; Abstractions must be merged/internalized into the plugin DLL."
    }

    # Step 4: Entrypoint validation (after all assembly modifications)
    # Verifies that declared entrypoint types exist in the built assembly
    if ($ResolveEntryPoints) {
        Write-Host "Validating entrypoint types..." -ForegroundColor Cyan
        Test-PluginManifest -Csproj $csprojPath -Manifest $manifestPath -PublishPath $publishPath -ResolveEntryPoints
        Write-Host "Entrypoint validation passed" -ForegroundColor Green
    }

    $manifestJson = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $pluginId = if ($manifestJson.id) { $manifestJson.id } else { $assemblyName }

    $packageRoot = Join-Path (Split-Path -Parent $csprojPath) 'artifacts/packages'
    if (-not (Test-Path -LiteralPath $packageRoot)) {
        New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    }

    # Emit package metadata capturing assembly versions and commit SHA for smoke verification
    try {
        $commit = (git rev-parse --short=8 HEAD) 2>$null
        if (-not $commit) { $commit = $env:GITHUB_SHA.Substring(0,8) }
    } catch { $commit = $null }

    $assemblyInfos = @()
    Get-ChildItem -LiteralPath $publishPath -Filter *.dll | ForEach-Object {
        $fv = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($_.FullName)
        $assemblyInfos += [pscustomobject]@{
            name = $_.Name
            fileVersion = $fv.FileVersion
            productVersion = $fv.ProductVersion
        }
    }

    $metadata = [pscustomobject]@{
        packageId = $pluginId
        version = $version
        framework = $Framework
        build = @{ commit = $(if ($commit) { $commit } else { 'unknown' }); date = (Get-Date).ToString('s') }
        assemblies = $assemblyInfos
    }
    $metadataPath = Join-Path $publishPath 'package-metadata.json'
    $metadata | ConvertTo-Json -Depth 5 | Set-Content -Path $metadataPath -Encoding UTF8

    $zipName = "{0}-{1}-{2}.zip" -f $pluginId, $version, $Framework
    $zipPath = Join-Path $packageRoot $zipName
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $publishPath '*') -DestinationPath $zipPath
    Assert-PluginPackageIdentity -ZipPath $zipPath -ExpectedVersion $version -RequireVersionedFileName -ValidateHostRequirements
    Write-Host "Created plugin package: $zipPath" -ForegroundColor Green

    # Post-package guardrail: canonical Abstractions sidecars are forbidden for
    # merged plugin packages.
    if ($doCanonicalInjection) {
        Write-Host "Verifying package does not ship Abstractions sidecar..." -ForegroundColor Cyan
        $verifyDir = Join-Path ([IO.Path]::GetTempPath()) "verify-package-$(Get-Random)"
        try {
            Expand-Archive -LiteralPath $zipPath -DestinationPath $verifyDir -Force
            $sidecar = Join-Path $verifyDir 'Lidarr.Plugin.Abstractions.dll'
            if (Test-Path -LiteralPath $sidecar) {
                throw "Package ships forbidden Lidarr.Plugin.Abstractions.dll sidecar; Abstractions must be merged/internalized."
            }
            Write-Host "[OK] Package verification passed" -ForegroundColor Green
        }
        finally {
            if (Test-Path $verifyDir) {
                Remove-Item -LiteralPath $verifyDir -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    return $zipPath
}

function Invoke-PluginCleanup {
    <#
    .SYNOPSIS
    Cleans up the publish output to match PluginPackaging.targets behavior.

    .DESCRIPTION
    Implements the canonical plugin packaging policy (matches `build/PluginPackaging.targets`).

    MUST SHIP:
      - Lidarr.Plugin.<Name>.dll (merged assembly)
      - plugin.json

    MUST NOT SHIP:
      - Lidarr.Plugin.Abstractions.dll (merged/internalized; sidecar breaks multi-plugin loading)
      - Lidarr.Plugin.Common.dll (merged/internalized)
      - FluentValidation.dll (host provides; shipping causes type-identity conflicts)
      - Microsoft.Extensions.DependencyInjection.Abstractions.dll (host provides; shipping breaks DI contracts)
      - Microsoft.Extensions.Logging.Abstractions.dll (host provides; shipping breaks ILogger contracts)
      - System.Text.Json.dll (cross-boundary type identity risk)
      - Host assemblies (Lidarr.*.dll, NzbDrone.*.dll, etc.)
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishPath,
        [Parameter(Mandatory = $true)]
        [string]$AssemblyName,
        [string[]]$AdditionalKeep = @()
    )

    # Canonical keep list - these assemblies belong in the package.
    # Everything else is either merged/internalized or host-provided.
    $keepPatterns = @(
        "$AssemblyName.dll"                                     # Plugin itself (merged)
    )

    # Add any plugin-specific additional assemblies (maps to PluginPackagingAdditionalKeep)
    if ($AdditionalKeep.Count -gt 0) {
        $keepPatterns += $AdditionalKeep
    }

    # Guardrail: refuse to keep assemblies that are explicitly forbidden by the packaging policy.
    # Exception: Lidarr.Plugin.*.dll are plugin-specific assemblies, NOT host assemblies.
    foreach ($pattern in $AdditionalKeep) {
        $isHostAssembly = ($pattern -like 'Lidarr.*.dll' -and $pattern -notlike 'Lidarr.Plugin.*.dll') -or $pattern -like 'NzbDrone.*.dll'
        if ($isHostAssembly) {
            throw "Refusing to keep host assembly pattern '$pattern'. Host assemblies (Lidarr.* / NzbDrone.*) must never be shipped."
        }
    }

    $forbiddenNames = @(
        'FluentValidation.dll',
        'Microsoft.Extensions.DependencyInjection.Abstractions.dll',
        'Microsoft.Extensions.Logging.Abstractions.dll',
        'System.Text.Json.dll',
        'Lidarr.Plugin.Abstractions.dll',
        'Lidarr.Plugin.Common.dll'
    )
    foreach ($forbidden in $forbiddenNames) {
        $shouldKeepForbidden = $keepPatterns | Where-Object { $forbidden -like $_ } | Select-Object -First 1
        if ($shouldKeepForbidden) {
            throw "Refusing to keep forbidden host-shared assembly '$forbidden' (matches keep pattern '$shouldKeepForbidden')."
        }
    }

    # Remove everything except kept assemblies
    $allDlls = Get-ChildItem -LiteralPath $PublishPath -Filter '*.dll'
    foreach ($dll in $allDlls) {
        $isKept = $false
        foreach ($pattern in $keepPatterns) {
            if ($dll.Name -like $pattern) {
                $isKept = $true
                break
            }
        }
        if (-not $isKept) {
            Write-Host "  Removing: $($dll.Name)" -ForegroundColor DarkGray
            Remove-Item -LiteralPath $dll.FullName -Force -ErrorAction SilentlyContinue
            
            # Also remove matching .pdb and .xml
            $pdb = [IO.Path]::ChangeExtension($dll.FullName, '.pdb')
            $xml = [IO.Path]::ChangeExtension($dll.FullName, '.xml')
            if (Test-Path -LiteralPath $pdb) { Remove-Item -LiteralPath $pdb -Force -ErrorAction SilentlyContinue }
            if (Test-Path -LiteralPath $xml) { Remove-Item -LiteralPath $xml -Force -ErrorAction SilentlyContinue }
        }
    }

    # Remove orphaned culture satellite directories. `dotnet build -o <shared>` also
    # lands the output of every project in the graph — including OutputItemType=Analyzer
    # references, whose Roslyn dependencies bring cs/de/... Microsoft.CodeAnalysis
    # satellite resources. The root-level sweep above never entered subdirectories, so
    # those satellites shipped in the zip (caught staging qobuzarr v0.5.12: 26 extra
    # DLLs). A satellite is kept only when its base assembly is kept.
    foreach ($cultureDir in Get-ChildItem -LiteralPath $PublishPath -Directory) {
        $satellites = @(Get-ChildItem -LiteralPath $cultureDir.FullName -Filter '*.resources.dll' -ErrorAction SilentlyContinue)
        foreach ($satellite in $satellites) {
            $baseDll = ($satellite.Name -replace '\.resources\.dll$', '.dll')
            $isKept = $false
            foreach ($pattern in $keepPatterns) {
                if ($baseDll -like $pattern) { $isKept = $true; break }
            }
            if (-not $isKept) {
                Write-Host "  Removing satellite: $($cultureDir.Name)/$($satellite.Name)" -ForegroundColor DarkGray
                Remove-Item -LiteralPath $satellite.FullName -Force -ErrorAction SilentlyContinue
            }
        }
        if (-not (Get-ChildItem -LiteralPath $cultureDir.FullName -ErrorAction SilentlyContinue)) {
            Remove-Item -LiteralPath $cultureDir.FullName -Force -ErrorAction SilentlyContinue
        }
    }

    # Remove deps.json (not needed for plugin)
    Get-ChildItem -LiteralPath $PublishPath -Filter '*.deps.json' | Remove-Item -Force -ErrorAction SilentlyContinue

    # Remove NuGet pack artifacts (not used at runtime, and can appear in output when OutDir is overridden)
    Get-ChildItem -LiteralPath $PublishPath -Filter '*.nupkg' | Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $PublishPath -Filter '*.snupkg' | Remove-Item -Force -ErrorAction SilentlyContinue

    # Remove runtimes folder (native dependencies should be handled by host)
    $runtimesPath = Join-Path $PublishPath 'runtimes'
    if (Test-Path -LiteralPath $runtimesPath) {
        Remove-Item -LiteralPath $runtimesPath -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host "Cleanup complete. Kept assemblies:" -ForegroundColor Cyan
    Get-ChildItem -LiteralPath $PublishPath -Filter '*.dll' | ForEach-Object { Write-Host "  $($_.Name)" -ForegroundColor Green }
}

function Invoke-PluginMerge {
    <#
    .SYNOPSIS
    Merges plugin dependencies using ILRepack, matching PluginPackaging.targets behavior.
    
    .DESCRIPTION
    Merges assemblies that should be internalized into the plugin:
    - Lidarr.Plugin.Common.dll
    - Polly*.dll
    - TagLibSharp*.dll
    - Microsoft.Extensions.DependencyInjection.dll (implementation, not abstractions)
    - Lidarr.Plugin.Abstractions.dll
    - Microsoft.Extensions.Caching.*.dll
    - Microsoft.Extensions.Options.dll
    - Microsoft.Extensions.Primitives.dll
    - Microsoft.Extensions.Http.dll
    
    Does NOT merge (type identity with host):
    - FluentValidation.dll
    - Microsoft.Extensions.DependencyInjection.Abstractions.dll
    - Microsoft.Extensions.Logging.Abstractions.dll
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishPath,
        [Parameter(Mandatory = $true)]
        [string]$AssemblyName,
        [Parameter(Mandatory = $true)]
        [string]$IlRepackRsp,
        [Parameter(Mandatory = $true)]
        [string]$InternalizeExclude
    )

    $pluginAssembly = Join-Path $PublishPath "$AssemblyName.dll"
    if (-not (Test-Path -LiteralPath $pluginAssembly)) {
        throw "Plugin assembly '$AssemblyName.dll' not found under $PublishPath."
    }

    # Assemblies to merge (matching PluginPackaging.targets _PluginDeps)
    # These get internalized into the plugin assembly
    # NOTE: Do NOT merge System.Text.Json - its types may cross the host/plugin boundary
    #       and cause TypeLoadException or weird serialization issues
    $mergePatterns = @(
        'Lidarr.Plugin.Common.dll',
        'Lidarr.Plugin.Abstractions.dll',
        'Polly.dll',
        'Polly.Core.dll', 
        'Polly.Extensions.Http.dll',
        'TagLibSharp*.dll',
        'Microsoft.Extensions.DependencyInjection.dll',  # Implementation, not abstractions
        'Microsoft.Extensions.Caching.Abstractions.dll',
        'Microsoft.Extensions.Caching.Memory.dll',
        'Microsoft.Extensions.Options.dll',
        'Microsoft.Extensions.Primitives.dll',
        'Microsoft.Extensions.Http.dll'
        # System.Text.Json.dll - EXCLUDED: types cross host/plugin boundary
    )

    $mergeCandidates = @()
    foreach ($pattern in $mergePatterns) {
        $matches = Get-ChildItem -LiteralPath $PublishPath -Filter $pattern -ErrorAction SilentlyContinue
        $mergeCandidates += $matches
    }

    if ($mergeCandidates.Count -eq 0) {
        Write-Host 'No candidate assemblies found for ILRepack. Skipping merge.' -ForegroundColor Yellow
        return
    }

    # Verify ilrepack is available before attempting merge
    $ilrepackPath = Get-Command 'ilrepack' -ErrorAction SilentlyContinue
    if (-not $ilrepackPath) {
        throw @"
ILRepack not found on PATH. The -MergeAssemblies flag requires ILRepack to be installed.

Options:
  1. (Recommended) Don't use -MergeAssemblies. MSBuild PluginPackaging.targets handles
     assembly merging during the build step automatically.
  2. Install an ILRepack CLI and ensure the `ilrepack` executable is on PATH.

Most plugins should NOT use -MergeAssemblies since the csproj build already merges.
"@
    }

    Write-Host "Merging assemblies:" -ForegroundColor Cyan
    $mergeCandidates | ForEach-Object { Write-Host "  $($_.Name)" -ForegroundColor DarkGray }

    $mergeOut = Join-Path $PublishPath "$AssemblyName.merged.dll"
    $resolvedRsp = Resolve-Path -LiteralPath $IlRepackRsp
    $resolvedExclude = Resolve-Path -LiteralPath $InternalizeExclude

    $rspContent = Get-Content -LiteralPath $resolvedRsp -Raw
    $rspContent = $rspContent.Replace('$(PublishDir)', $PublishPath).Replace('$(MergeOut)', $mergeOut).Replace('$(InternalizeExclude)', $resolvedExclude)
    $tempRsp = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName() + '.rsp')
    Set-Content -Path $tempRsp -Value $rspContent -Encoding UTF8

    $argumentList = @("@$tempRsp")
    $argumentList += $pluginAssembly
    $argumentList += $mergeCandidates.FullName

    & ilrepack $argumentList
    $exit = $LASTEXITCODE

    Remove-Item -LiteralPath $tempRsp -ErrorAction SilentlyContinue

    if ($exit -ne 0) {
        throw "ilrepack failed with exit code $exit."
    }

    Move-Item -LiteralPath $mergeOut -Destination $pluginAssembly -Force
    $mergedPdb = [IO.Path]::ChangeExtension($mergeOut, '.pdb')
    if (Test-Path -LiteralPath $mergedPdb) {
        $targetPdb = [IO.Path]::ChangeExtension($pluginAssembly, '.pdb')
        Move-Item -LiteralPath $mergedPdb -Destination $targetPdb -Force
    }

    foreach ($candidate in $mergeCandidates) {
        Remove-Item -LiteralPath $candidate.FullName -Force
        $pdbPath = [IO.Path]::ChangeExtension($candidate.FullName, '.pdb')
        if (Test-Path -LiteralPath $pdbPath) {
            Remove-Item -LiteralPath $pdbPath -Force
        }
    }
}

function Get-CanonicalAbstractionsConfig {
    <#
    .SYNOPSIS
    Reads the canonical-abstractions.json configuration file.

    .DESCRIPTION
    Returns the pinned version, Common SHA, and Abstractions SHA256 from the
    canonical-abstractions.json file. This ensures reproducible builds.
    #>
    [CmdletBinding()]
    param()

    $scriptRoot = Split-Path -Parent $PSCommandPath
    $configPath = Join-Path $scriptRoot 'canonical-abstractions.json'

    if (-not (Test-Path $configPath)) {
        return $null
    }

    try {
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        return @{
            Version = $config.version
            CommonSha = $config.commonSha
            AbstractionsSha256 = $config.abstractionsSha256
            ReleaseUrl = $config.releaseUrl
        }
    }
    catch {
        Write-Warning "Failed to parse canonical-abstractions.json: $_"
        return $null
    }
}

function Install-CanonicalAbstractions {
    <#
    .SYNOPSIS
    Legacy helper that injects the canonical Lidarr.Plugin.Abstractions.dll into the publish output.

    .DESCRIPTION
    Downloads the canonical Abstractions DLL from a Common release (or uses a local cache),
    replaces the build output's copy, and verifies the SHA256 hash matches. This ensures all
    plugins use the exact same Abstractions binary, eliminating drift.

    Current plugin packages should not use this helper; Abstractions is merged/internalized into
    the plugin DLL and sidecars are rejected by New-PluginPackage.

    HARD GATE: Build fails if verification fails.

    .PARAMETER PublishPath
        Directory containing the plugin build output.

    .PARAMETER CommonVersion
        Version of Common to download Abstractions from (e.g., "1.5.0").
        If not provided, reads from canonical-abstractions.json.

    .PARAMETER ExpectedSha256
        Expected SHA256 hash. If not provided, reads from canonical-abstractions.json
        or uses the hash from the release's .sha256 file.

    .PARAMETER CanonicalAbstractionsPath
        Optional local path to a pre-downloaded Abstractions.dll.
        Use this for offline builds or Docker environments without GitHub access.

    .PARAMETER CacheDirectory
        Optional directory to cache downloaded Abstractions. Defaults to $env:TEMP/canonical-abstractions-cache.
        If the file exists and hash matches, download is skipped.

    .PARAMETER Repository
        GitHub repository for Common. Defaults to "RicherTunes/Lidarr.Plugin.Common".
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishPath,

        [string]$CommonVersion,

        [string]$ExpectedSha256,

        [string]$CanonicalAbstractionsPath,

        [string]$CacheDirectory,

        [string]$Repository = "RicherTunes/Lidarr.Plugin.Common"
    )

    $ErrorActionPreference = 'Stop'
    $scriptRoot = Split-Path -Parent $PSCommandPath

    # Load config from canonical-abstractions.json if parameters not provided
    $config = Get-CanonicalAbstractionsConfig
    if (-not $CommonVersion -and $config) {
        $CommonVersion = $config.Version
        Write-Host "Using version from canonical-abstractions.json: $CommonVersion" -ForegroundColor DarkGray
    }
    if (-not $ExpectedSha256 -and $config) {
        $ExpectedSha256 = $config.AbstractionsSha256
        $sha256Preview = $ExpectedSha256.Substring(0, 16) + '...'
        Write-Host "Using SHA256 from canonical-abstractions.json: $sha256Preview" -ForegroundColor DarkGray
    }

    if (-not $CommonVersion) {
        throw "CommonVersion is required. Provide -CommonVersion or ensure canonical-abstractions.json exists."
    }

    Write-Host "Installing canonical Abstractions from Common v$CommonVersion..." -ForegroundColor Cyan

    # Option 1: Use local path if provided
    if ($CanonicalAbstractionsPath -and (Test-Path $CanonicalAbstractionsPath)) {
        Write-Host "Using local Abstractions from: $CanonicalAbstractionsPath" -ForegroundColor DarkGray
        $canonicalDll = $CanonicalAbstractionsPath
    }
    else {
        # Option 2: Check cache directory
        if (-not $CacheDirectory) {
            $CacheDirectory = Join-Path ([IO.Path]::GetTempPath()) 'canonical-abstractions-cache'
        }
        $cachedDll = Join-Path $CacheDirectory "v$CommonVersion" 'Lidarr.Plugin.Abstractions.dll'

        if ((Test-Path $cachedDll) -and $ExpectedSha256) {
            $cachedHash = (Get-FileHash -Path $cachedDll -Algorithm SHA256).Hash.ToLower()
            if ($cachedHash -eq $ExpectedSha256.ToLower()) {
                Write-Host "Using cached Abstractions (hash verified)" -ForegroundColor DarkGray
                $canonicalDll = $cachedDll
            }
        }

        # Option 3: Download from GitHub
        if (-not $canonicalDll -or -not (Test-Path $canonicalDll)) {
            $tempDir = Join-Path ([IO.Path]::GetTempPath()) "canonical-abstractions-$(Get-Random)"
            New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

            try {
                $getCanonicalScript = Join-Path $scriptRoot 'Get-CanonicalAbstractions.ps1'

                if (-not (Test-Path $getCanonicalScript)) {
                    throw "Get-CanonicalAbstractions.ps1 not found at $getCanonicalScript"
                }

                $canonicalDll = & $getCanonicalScript -Version $CommonVersion -OutputPath $tempDir -Repository $Repository
                if (-not $canonicalDll -or -not (Test-Path $canonicalDll)) {
                    throw "Failed to download canonical Abstractions.dll"
                }

                # Cache for future use and update $canonicalDll to the cached path
                # (the temp directory is cleaned up in the finally block)
                $cacheDir = Join-Path $CacheDirectory "v$CommonVersion"
                if (-not (Test-Path $cacheDir)) {
                    New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
                }
                $cachedDllPath = Join-Path $cacheDir 'Lidarr.Plugin.Abstractions.dll'
                Copy-Item -LiteralPath $canonicalDll -Destination $cachedDllPath -Force
                $pdbPath = Join-Path $tempDir 'Lidarr.Plugin.Abstractions.pdb'
                if (Test-Path $pdbPath) {
                    Copy-Item -LiteralPath $pdbPath -Destination (Join-Path $cacheDir 'Lidarr.Plugin.Abstractions.pdb') -Force
                }
                $canonicalDll = $cachedDllPath
                Write-Host "Cached Abstractions for future builds" -ForegroundColor DarkGray
            }
            finally {
                if ((Test-Path $tempDir) -and $tempDir -ne $CacheDirectory) {
                    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
                }
            }
        }
    }

    # Calculate hash of DLL
    $actualHash = (Get-FileHash -Path $canonicalDll -Algorithm SHA256).Hash.ToLower()

    # Verify against expected hash if provided
    if ($ExpectedSha256) {
        $expected = $ExpectedSha256.ToLower().Trim()
        if ($actualHash -ne $expected) {
            throw "CANONICAL ABSTRACTIONS SHA256 MISMATCH!`nExpected: $expected`nActual:   $actualHash`nThis is a HARD GATE failure - packaging cannot proceed."
        }
        Write-Host "[OK] SHA256 verified against expected hash" -ForegroundColor Green
    }

    # Replace the build output's Abstractions.dll
    $targetDll = Join-Path $PublishPath 'Lidarr.Plugin.Abstractions.dll'
    Copy-Item -LiteralPath $canonicalDll -Destination $targetDll -Force

    # Also copy PDB if available (check cache or local path)
    $pdbSource = $null
    if ($CanonicalAbstractionsPath) {
        $pdbSource = [IO.Path]::ChangeExtension($CanonicalAbstractionsPath, '.pdb')
    }
    elseif ($CacheDirectory) {
        $pdbSource = Join-Path $CacheDirectory "v$CommonVersion" 'Lidarr.Plugin.Abstractions.pdb'
    }
    if ($pdbSource -and (Test-Path $pdbSource)) {
        $targetPdb = Join-Path $PublishPath 'Lidarr.Plugin.Abstractions.pdb'
        Copy-Item -LiteralPath $pdbSource -Destination $targetPdb -Force
    }

    Write-Host "[OK] Installed canonical Abstractions.dll (SHA256: $actualHash)" -ForegroundColor Green

    return @{
        Path = $targetDll
        Sha256 = $actualHash
        Version = $CommonVersion
    }
}

function Assert-CanonicalAbstractions {
    <#
    .SYNOPSIS
    Verifies the Abstractions.dll in publish output matches the canonical hash.

    .DESCRIPTION
    HARD GATE: Fails the build if the hash doesn't match the expected canonical hash.
    Use this after packaging to verify the ZIP contains the correct Abstractions.

    .PARAMETER Path
        Path to the Abstractions.dll to verify (or directory containing it).

    .PARAMETER ExpectedSha256
        Expected SHA256 hash (canonical hash from Common release).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedSha256
    )

    $ErrorActionPreference = 'Stop'

    # Handle both file and directory paths
    $dllPath = if (Test-Path $Path -PathType Container) {
        Join-Path $Path 'Lidarr.Plugin.Abstractions.dll'
    } else {
        $Path
    }

    if (-not (Test-Path $dllPath)) {
        throw "HARD GATE FAILURE: Abstractions.dll not found at $dllPath"
    }

    $actualHash = (Get-FileHash -Path $dllPath -Algorithm SHA256).Hash.ToLower()
    $expected = $ExpectedSha256.ToLower().Trim()

    if ($actualHash -ne $expected) {
        throw @"
╔═══════════════════════════════════════════════════════════════════════════╗
║                    CANONICAL ABSTRACTIONS SHA256 MISMATCH                  ║
╠═══════════════════════════════════════════════════════════════════════════╣
║  Expected: $expected                         ║
║  Actual:   $actualHash                         ║
╠═══════════════════════════════════════════════════════════════════════════╣
║  HARD GATE: Packaging cannot proceed.                                      ║
║  The Abstractions.dll in the package does not match the canonical binary.  ║
║  Use Install-CanonicalAbstractions to inject the correct version.          ║
╚═══════════════════════════════════════════════════════════════════════════╝
"@
    }

    $hashPrefix = $expected.Substring(0, 16) + '...'
    Write-Host "[OK] Abstractions.dll verified: SHA256 matches canonical ($hashPrefix)" -ForegroundColor Green
    return $true
}

Export-ModuleMember -Function Get-PluginOutput, Test-PluginManifest, New-PluginPackage, Resolve-PluginPackVersion, Assert-PluginPackageIdentity, Invoke-PluginCleanup, Assert-PluginAssemblyHasNoMergedReferences, Get-CanonicalAbstractionsConfig, Install-CanonicalAbstractions, Assert-CanonicalAbstractions
# end-snippet
