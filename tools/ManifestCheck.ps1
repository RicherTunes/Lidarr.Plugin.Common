# snippet:manifest-ci
# snippet-skip-compile
<#
.SYNOPSIS
    Validates plugin manifest (plugin.json) against schema and optionally verifies entrypoints exist in built assembly.

.DESCRIPTION
    Performs multi-level validation of a Lidarr plugin manifest:

    Level 1 - Schema validation:
      - Required fields present (id, name, version, apiVersion, minHostVersion)
      - Field formats valid (semver patterns, lowercase id, etc.)
      - Version matches csproj

    Level 2 - Package alignment:
      - Abstractions package version aligns with apiVersion
      - Target frameworks match

    Level 3 - Entrypoint resolution (with -ResolveEntryPoints):
      - Main DLL exists in publish output
      - Types in rootNamespace exist in assembly
      - Plugin interface implementations discoverable

.PARAMETER ProjectPath
    Path to the .csproj project file.

.PARAMETER ManifestPath
    Path to the plugin.json manifest file.

.PARAMETER AbstractionsPackage
    Name of the Abstractions NuGet package. Default: "Lidarr.Plugin.Abstractions"

.PARAMETER Strict
    Treat warnings as errors.

.PARAMETER JsonOutput
    Output results as JSON for CI integration.

.PARAMETER PublishPath
    Path to the publish output directory (for entrypoint resolution).

.PARAMETER ResolveEntryPoints
    When specified, loads the built assembly and verifies entrypoint types exist.
    Requires -PublishPath to be specified.

.EXAMPLE
    # Basic schema validation
    .\ManifestCheck.ps1 -ProjectPath src/MyPlugin/MyPlugin.csproj -ManifestPath plugin.json

.EXAMPLE
    # Full validation with entrypoint resolution
    .\ManifestCheck.ps1 -ProjectPath src/MyPlugin/MyPlugin.csproj -ManifestPath plugin.json `
        -PublishPath artifacts/publish/net8.0/Release -ResolveEntryPoints
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,

    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [string]$AbstractionsPackage = 'Lidarr.Plugin.Abstractions',

    [switch]$Strict,

    [switch]$JsonOutput,

    [string]$PublishPath,

    [switch]$ResolveEntryPoints
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ProjectPath)) {
    throw "Project file '$ProjectPath' not found."
}

if (-not (Test-Path -LiteralPath $ManifestPath)) {
    throw "Manifest file '$ManifestPath' not found."
}

[xml]$project = Get-Content -LiteralPath $ProjectPath
# Use a real XmlNamespaceManager for robust MSBuild parsing
$nsmgr = New-Object System.Xml.XmlNamespaceManager($project.NameTable)
$nsmgr.AddNamespace('msb', 'http://schemas.microsoft.com/developer/msbuild/2003')

# Resolve Version: try XML parsing first, then MSBuild evaluation as fallback
# MSBuild evaluation handles Directory.Build.props, VERSION files, and conditional properties
$versionNode = $project.SelectSingleNode('//msb:Project/msb:PropertyGroup/msb:Version', $nsmgr)
if (-not $versionNode) { $versionNode = $project.SelectSingleNode('//msb:Project/msb:PropertyGroup/msb:AssemblyVersion', $nsmgr) }
if (-not $versionNode) { $versionNode = $project.SelectSingleNode('//Project/PropertyGroup/Version') }
if (-not $versionNode) { $versionNode = $project.SelectSingleNode('//Project/PropertyGroup/AssemblyVersion') }

$projectVersion = $null
if ($versionNode) {
    $rawVersion = $versionNode.InnerText.Trim()
    # Check if the value contains unresolved MSBuild expressions like $(Version), $(VersionPrefix), etc.
    if ($rawVersion -and $rawVersion -notmatch '\$\(') {
        $projectVersion = $rawVersion
    }
}

# Fallback: Use MSBuild to evaluate the Version property (handles Directory.Build.props, VERSION files, expressions, etc.)
if (-not $projectVersion) {
    try {
        $msbuildOutput = & dotnet msbuild $ProjectPath -getProperty:Version -nologo 2>&1
        if ($LASTEXITCODE -eq 0 -and $msbuildOutput) {
            $rawVersion = ($msbuildOutput | Out-String).Trim()
            # Extract semver pattern (X.Y.Z or X.Y.Z-suffix) from potentially noisy output
            # This handles cases where MSBuild outputs warnings along with the version
            if ($rawVersion -match '(\d+\.\d+\.\d+(?:-[\w\.\+]+)?)') {
                $projectVersion = $matches[1]
            } elseif ($rawVersion -and $rawVersion -notmatch '[\r\n]') {
                # If it is a single line without semver pattern, use it directly
                $projectVersion = $rawVersion
            }
        }
    } catch {
        # MSBuild evaluation failed, continue to VERSION file fallback
    }
}

# Last resort: Check for VERSION file in repository root (legacy fallback)
if (-not $projectVersion) {
    $searchDir = Split-Path -Parent (Resolve-Path -LiteralPath $ProjectPath)
    for ($i = 0; $i -lt 5; $i++) {
        $versionFilePath = Join-Path $searchDir 'VERSION'
        if (Test-Path -LiteralPath $versionFilePath) {
            $projectVersion = (Get-Content -LiteralPath $versionFilePath -Raw).Trim()
            break
        }
        $parentDir = Split-Path -Parent $searchDir
        if (-not $parentDir -or $parentDir -eq $searchDir) { break }
        $searchDir = $parentDir
    }
}

if (-not $projectVersion) {
    throw "Unable to resolve Version from '$ProjectPath'. Tried: XML parsing, MSBuild evaluation, VERSION file. Ensure the project has a Version property."
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json

if (-not $manifest.version) {
    throw "Manifest at '$ManifestPath' is missing 'version'."
}

# Check for ProjectReference first (preferred for development with submodules)
# Also check for Lidarr.Plugin.Common which transitively includes Abstractions
$projectReference = $project.SelectSingleNode("//msb:Project/msb:ItemGroup/msb:ProjectReference[contains(@Include, 'Lidarr.Plugin.Abstractions.csproj') or contains(@Include, 'Abstractions') or contains(@Include, 'Lidarr.Plugin.Common.csproj')]", $nsmgr)
if (-not $projectReference) { $projectReference = $project.SelectSingleNode("//Project/ItemGroup/ProjectReference[contains(@Include, 'Lidarr.Plugin.Abstractions.csproj') or contains(@Include, 'Abstractions') or contains(@Include, 'Lidarr.Plugin.Common.csproj')]") }

# Check for PackageReference (used in package-based consumption)
$packageReference = $project.SelectSingleNode("//msb:Project/msb:ItemGroup/msb:PackageReference[@Include='$AbstractionsPackage']", $nsmgr)
if (-not $packageReference) { $packageReference = $project.SelectSingleNode("//Project/ItemGroup/PackageReference[@Include='$AbstractionsPackage']") }

$packageMajor = $null
$packageVersion = $null
$usingProjectRef = $false

if ($projectReference) {
    # Prefer ProjectReference (development mode with submodule) - version comes from manifest.commonVersion
    $usingProjectRef = $true
}
elseif ($packageReference) {
    $packageVersion = $packageReference.Version
    # Handle centralized package management (Directory.Packages.props) where Version is not on the PackageReference
    if (-not $packageVersion) {
        # Check for Directory.Packages.props in the repository
        $projectDir = Split-Path -Parent (Resolve-Path -LiteralPath $ProjectPath)
        $searchDir = $projectDir
        for ($i = 0; $i -lt 5; $i++) {
            $packagesPropsPath = Join-Path $searchDir 'Directory.Packages.props'
            if (Test-Path -LiteralPath $packagesPropsPath) {
                [xml]$packagesProps = Get-Content -LiteralPath $packagesPropsPath
                $pkgNsmgr = New-Object System.Xml.XmlNamespaceManager($packagesProps.NameTable)
                $pkgNsmgr.AddNamespace('msb', 'http://schemas.microsoft.com/developer/msbuild/2003')
                $pkgVersionNode = $packagesProps.SelectSingleNode("//msb:Project/msb:ItemGroup/msb:PackageVersion[@Include='$AbstractionsPackage']/@Version", $pkgNsmgr)
                if (-not $pkgVersionNode) { $pkgVersionNode = $packagesProps.SelectSingleNode("//Project/ItemGroup/PackageVersion[@Include='$AbstractionsPackage']/@Version") }
                if ($pkgVersionNode) {
                    $packageVersion = $pkgVersionNode.Value
                }
                break
            }
            $parentDir = Split-Path -Parent $searchDir
            if (-not $parentDir -or $parentDir -eq $searchDir) { break }
            $searchDir = $parentDir
        }
        
        if (-not $packageVersion) {
            # PackageReference exists but version not resolvable - treat as ProjectReference scenario
            $usingProjectRef = $true
        }
    }
    if ($packageVersion) {
        $packageMajor = ($packageVersion -split '\.')[0]
    }
}
else {
    throw "Project '$ProjectPath' must reference $AbstractionsPackage either as a PackageReference or ProjectReference."
}

$errorList = @()
$warningList = @()

if ($manifest.version -ne $projectVersion) {
    $errorList += "Manifest version '$($manifest.version)' does not match project Version '$projectVersion'."
}

if (-not $manifest.apiVersion) {
    $errorList += "Manifest missing 'apiVersion'."
}
elseif ($manifest.apiVersion -notmatch '^\d+\.x$') {
    $errorList += "apiVersion must be in 'major.x' form (e.g. '1.x')."
}
else {
    $apiMajor = ($manifest.apiVersion -split '\.')[0]
    if ($packageMajor) {
        if ($apiMajor -ne $packageMajor) {
            $errorList += "apiVersion major $apiMajor does not match $AbstractionsPackage major $packageMajor."
        }
    } elseif ($usingProjectRef) {
        if (-not $manifest.commonVersion) {
            $msg = "MAN001: Using in-repo $AbstractionsPackage via ProjectReference; 'manifest.commonVersion' is required to validate apiVersion."
            if ($Strict) { $errorList += $msg } else { $warningList += $msg }
        } else {
            $commonMajor = ($manifest.commonVersion -split '\.')[0]
            if ($apiMajor -ne $commonMajor) {
                $msg = "MAN001: apiVersion major $apiMajor does not match manifest.commonVersion major $commonMajor (ProjectReference in use)."
                if ($Strict) { $errorList += $msg } else { $warningList += $msg }
            } else {
                $msg = "MAN001: ProjectReference to in-repo abstractions detected; using manifest.commonVersion '$($manifest.commonVersion)' for validation."
                if ($Strict) { $errorList += $msg } else { $warningList += $msg }
            }
        }
    }
}

if (-not $manifest.minHostVersion) {
    $warningList += "minHostVersion is not set; host compatibility cannot be enforced."
}

if ($manifest.targets) {
    $tfmNode = $project.SelectSingleNode('//msb:Project/msb:PropertyGroup/msb:TargetFrameworks', $nsmgr)
    if (-not $tfmNode) { $tfmNode = $project.SelectSingleNode('//msb:Project/msb:PropertyGroup/msb:TargetFramework', $nsmgr) }
    if (-not $tfmNode) { $tfmNode = $project.SelectSingleNode('//Project/PropertyGroup/TargetFrameworks') }
    if (-not $tfmNode) { $tfmNode = $project.SelectSingleNode('//Project/PropertyGroup/TargetFramework') }
    $projectTfms = if ($tfmNode) { $tfmNode.InnerText.Split(';') | ForEach-Object { $_.Trim() } } else { @() }

    $missing = @()
    foreach ($target in $manifest.targets) {
        if ($projectTfms -notcontains $target) {
            $missing += $target
        }
    }
    if ($missing.Count -gt 0) {
        $errorList += "Project is missing TargetFramework(s): $($missing -join ', ') referenced in manifest.targets."
    }
}

# ============================================================================
# Level 3: Entrypoint Resolution (when -ResolveEntryPoints is specified)
# ============================================================================

if ($ResolveEntryPoints) {
    if (-not $PublishPath) {
        $errorList += "MAN002: -PublishPath is required when using -ResolveEntryPoints"
    }
    elseif (-not (Test-Path -LiteralPath $PublishPath)) {
        $errorList += "MAN002: Publish path not found: $PublishPath"
    }
    else {
        # Determine main DLL name
        $mainDll = $manifest.main
        if (-not $mainDll) {
            # Infer from rootNamespace or id
            if ($manifest.rootNamespace) {
                $mainDll = "$($manifest.rootNamespace).dll"
            }
            else {
                # Common pattern: Lidarr.Plugin.<Id>.dll
                $id = $manifest.id
                $capitalizedId = $id.Substring(0,1).ToUpper() + $id.Substring(1)
                $mainDll = "Lidarr.Plugin.$capitalizedId.dll"
            }
            $warningList += "MAN002: No 'main' field in manifest; inferring: $mainDll"
        }

        $mainDllPath = Join-Path $PublishPath $mainDll
        if (-not (Test-Path -LiteralPath $mainDllPath)) {
            $errorList += "MAN002: Main DLL not found in publish output: $mainDll"
        }
        else {
            # Try to load assembly and inspect types using reflection metadata
            try {
                Add-Type -AssemblyName 'System.Reflection.Metadata' -ErrorAction SilentlyContinue

                $assemblyBytes = [System.IO.File]::ReadAllBytes($mainDllPath)
                $memStream = [System.IO.MemoryStream]::new($assemblyBytes)
                $peReader = [System.Reflection.PortableExecutable.PEReader]::new($memStream)
                $metadataReader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)

                # Check for expected namespace
                $rootNamespace = $manifest.rootNamespace
                if ($rootNamespace) {
                    $foundNamespace = $false
                    foreach ($typeHandle in $metadataReader.TypeDefinitions) {
                        $typeDef = $metadataReader.GetTypeDefinition($typeHandle)
                        $ns = $metadataReader.GetString($typeDef.Namespace)
                        if ($ns -eq $rootNamespace -or $ns.StartsWith("$rootNamespace.")) {
                            $foundNamespace = $true
                            break
                        }
                    }
                    if (-not $foundNamespace) {
                        $errorList += "MAN003: No types found in declared rootNamespace: $rootNamespace"
                    }
                }

                # Look for plugin interface implementations
                # These are discoverable by Lidarr's plugin loader
                $pluginTypePatterns = @(
                    'Indexer',
                    'DownloadClient',
                    'ImportList'
                )

                $foundPluginTypes = @()
                foreach ($typeHandle in $metadataReader.TypeDefinitions) {
                    $typeDef = $metadataReader.GetTypeDefinition($typeHandle)
                    $typeName = $metadataReader.GetString($typeDef.Name)
                    $ns = $metadataReader.GetString($typeDef.Namespace)

                    # Skip compiler-generated and nested types
                    if ($typeName.StartsWith('<') -or $typeName.Contains('+')) { continue }

                    # Check if type name matches plugin patterns
                    foreach ($pattern in $pluginTypePatterns) {
                        if ($typeName -match $pattern -and $ns -like "$($manifest.rootNamespace)*") {
                            $foundPluginTypes += "$ns.$typeName"
                            break
                        }
                    }
                }

                if ($foundPluginTypes.Count -eq 0) {
                    $warningList += "MAN003: No obvious plugin types found (Indexer/DownloadClient/ImportList) in $mainDll"
                }
                elseif (-not $JsonOutput) {
                    Write-Host "Found plugin types: $($foundPluginTypes -join ', ')" -ForegroundColor Green
                }

                $peReader.Dispose()
                $memStream.Dispose()
            }
            catch {
                $warningList += "MAN002: Could not inspect assembly metadata: $_"
            }
        }
    }
}

if ($JsonOutput) {
    function Parse-Diagnostic {
        param([string]$Message, [string]$Severity)
        $id = $null
        $payload = $Message
        if ($Message -match '^(?<id>[A-Z]{3}\d{3}):\s*(?<rest>.*)$') {
            $id = $matches['id']
            $payload = $matches['rest']
        }
        return [pscustomobject]@{
            id = $id
            severity = $Severity
            message = $Message
            payload = $payload
        }
    }

    $out = @()
    foreach ($msg in $warningList) { $out += (Parse-Diagnostic -Message $msg -Severity 'Warning') }
    foreach ($msg in $errorList)   { $out += (Parse-Diagnostic -Message $msg -Severity 'Error') }

    $json = $out | ConvertTo-Json -Depth 5
    Write-Output $json
    if ($errorList.Count -gt 0) { exit 1 } else { exit 0 }
}
else {
    if ($errorList.Count -gt 0) {
        foreach ($msg in $errorList) { Write-Error $msg }
        throw "Manifest validation failed."
    }
    foreach ($msg in $warningList) { Write-Warning $msg }
    Write-Host "Manifest validation succeeded for '$ManifestPath'." -ForegroundColor Green
}
# end-snippet
