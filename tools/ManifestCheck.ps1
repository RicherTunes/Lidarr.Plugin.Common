# snippet:manifest-ci
# snippet-skip-compile
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,

    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [string]$AbstractionsPackage = 'Lidarr.Plugin.Abstractions',

    [switch]$ValidateEntryPoints,

    [string]$Configuration = 'Release',

    [string]$TargetFramework,

    [switch]$Strict,

    [switch]$JsonOutput
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

function Get-ProjectProperty {
    param([xml]$Project, [System.Xml.XmlNamespaceManager]$NsMgr, [string]$Name)
    $node = $Project.SelectSingleNode("//msb:Project/msb:PropertyGroup/msb:$Name", $NsMgr)
    if (-not $node) { $node = $Project.SelectSingleNode("//Project/PropertyGroup/$Name") }
    if ($node) { return $node.InnerText.Trim() }
    return $null
}

function Get-ProjectTargetFrameworks {
    param([xml]$Project, [System.Xml.XmlNamespaceManager]$NsMgr)
    $tfms = Get-ProjectProperty -Project $Project -NsMgr $NsMgr -Name 'TargetFrameworks'
    if (-not $tfms) { $tfms = Get-ProjectProperty -Project $Project -NsMgr $NsMgr -Name 'TargetFramework' }
    if (-not $tfms) { return @() }
    return $tfms.Split(';') | ForEach-Object { $_.Trim() } | Where-Object { $_ }
}

function Resolve-EntryPointAssemblyPaths {
    param(
        [pscustomobject]$Manifest,
        [xml]$Project,
        [System.Xml.XmlNamespaceManager]$NsMgr,
        [string]$ProjectPath,
        [string]$Configuration,
        [string]$TargetFramework
    )

    $tfm = $TargetFramework
    if (-not $tfm) {
        if ($Manifest.targets -and $Manifest.targets.Count -gt 0) {
            $tfm = $Manifest.targets[0]
        }
        elseif ($Manifest.targetFramework) {
            $tfm = [string]$Manifest.targetFramework
        }
        elseif ($Manifest.targetFrameworks -and $Manifest.targetFrameworks.Count -gt 0) {
            $tfm = $Manifest.targetFrameworks[0]
        }
        else {
            $tfm = (Get-ProjectTargetFrameworks -Project $Project -NsMgr $NsMgr | Select-Object -First 1)
        }
    }

    if (-not $tfm) {
        return [pscustomobject]@{ Tfm = $null; OutputDir = $null; AssemblyPaths = @() }
    }

    $projectDir = Split-Path -Parent (Resolve-Path -LiteralPath $ProjectPath)
    $outputDir = Join-Path $projectDir "bin/$Configuration/$tfm"

    $assemblies = @()
    if ($Manifest.assemblies -and $Manifest.assemblies.Count -gt 0) {
        foreach ($a in $Manifest.assemblies) { if ($a) { $assemblies += [string]$a } }
    }
    elseif ($Manifest.main) {
        $assemblies = @([string]$Manifest.main)
    }

    if ($assemblies.Count -eq 0) {
        $assemblyName = Get-ProjectProperty -Project $Project -NsMgr $NsMgr -Name 'AssemblyName'
        if (-not $assemblyName) { $assemblyName = [System.IO.Path]::GetFileNameWithoutExtension($ProjectPath) }
        $assemblies = @("$assemblyName.dll")
    }

    $paths = @()
    foreach ($file in $assemblies) { $paths += (Join-Path $outputDir $file) }

    return [pscustomobject]@{
        Tfm = $tfm
        OutputDir = $outputDir
        AssemblyPaths = $paths
    }
}

function Test-EntryPointTypesExist {
    param(
        [string]$OutputDir,
        [string[]]$AssemblyPaths,
        [pscustomobject[]]$EntryPoints
    )

    if (-not $EntryPoints -or $EntryPoints.Count -eq 0) { return @() }

    try {
        Add-Type -AssemblyName System.Reflection.Metadata -ErrorAction Stop | Out-Null
    } catch {
        return @("ENT000: Unable to load System.Reflection.Metadata required for entrypoint validation: $($_.Exception.Message)")
    }

    # Prefer explicit assembly paths (manifest.assemblies / manifest.main / project AssemblyName),
    # but fall back to scanning output directories when layout differs (e.g., custom output paths).
    $existingAssemblyPaths = @()
    foreach ($ap in $AssemblyPaths) {
        if ($ap -and (Test-Path -LiteralPath $ap)) { $existingAssemblyPaths += $ap }
    }

    if ($existingAssemblyPaths.Count -eq 0) {
        $candidateDirs = @()
        if ($OutputDir) {
            $candidateDirs += $OutputDir
            $candidateDirs += (Split-Path -Parent $OutputDir)
        }
        $candidateDirs = $candidateDirs | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique

        $discovered = @()
        foreach ($d in $candidateDirs) {
            $discovered += Get-ChildItem -LiteralPath $d -Filter '*.dll' -File -ErrorAction SilentlyContinue |
                ForEach-Object { $_.FullName }
        }
        if ($discovered.Count -eq 0) {
            foreach ($d in $candidateDirs) {
                $discovered += Get-ChildItem -LiteralPath $d -Filter '*.dll' -File -Recurse -ErrorAction SilentlyContinue |
                    ForEach-Object { $_.FullName }
            }
        }

        $existingAssemblyPaths = $discovered | Where-Object { $_ } | Select-Object -Unique
    }

    $typeIndex = @{}
    $ioErrors = @()
    foreach ($ap in $existingAssemblyPaths) {
        if (-not $ap -or -not (Test-Path -LiteralPath $ap)) { continue }

        try {
            $fs = [System.IO.File]::Open($ap, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        } catch {
            $ioErrors += "ENT000: Failed to open assembly for entrypoint validation: '$ap' ($($_.Exception.Message))"
            continue
        }

        try {
            $pe = [System.Reflection.PortableExecutable.PEReader]::new($fs)
            try {
                if (-not $pe.HasMetadata) { continue }

                $metaBlock = $pe.GetMetadata()
                $metaBytes = [byte[]]$metaBlock.GetContent()
                $ms = [System.IO.MemoryStream]::new($metaBytes, $false)
                $provider = [System.Reflection.Metadata.MetadataReaderProvider]::FromMetadataStream(
                    $ms,
                    [System.Reflection.Metadata.MetadataStreamOptions]::Default,
                    0)
                try {
                    $reader = $provider.GetMetadataReader(
                        [System.Reflection.Metadata.MetadataReaderOptions]::None,
                        [System.Reflection.Metadata.MetadataStringDecoder]::DefaultUTF8)

                    foreach ($handle in $reader.TypeDefinitions) {
                        $td = $reader.GetTypeDefinition($handle)
                        $name = $reader.GetString($td.Name)
                        if (-not $name -or $name -eq "<Module>") { continue }

                        $ns = $reader.GetString($td.Namespace)
                        $fullName = if ($ns) { "$ns.$name" } else { $name }

                        $attrs = [System.Reflection.TypeAttributes]$td.Attributes
                        $vis = $attrs -band [System.Reflection.TypeAttributes]::VisibilityMask
                        $isPublic = ($vis -eq [System.Reflection.TypeAttributes]::Public -or $vis -eq [System.Reflection.TypeAttributes]::NestedPublic)

                        if ($typeIndex.ContainsKey($fullName)) {
                            $typeIndex[$fullName] = ($typeIndex[$fullName] -or $isPublic)
                        } else {
                            $typeIndex[$fullName] = $isPublic
                        }
                    }
                }
                finally {
                    try { $provider.Dispose() } catch { }
                    try { $ms.Dispose() } catch { }
                }
            }
            finally { try { $pe.Dispose() } catch { } }
        } catch {
            $ioErrors += "ENT000: Failed to read assembly metadata for entrypoint validation: '$ap' ($($_.Exception.Message))"
            continue
        }
        finally { try { $fs.Dispose() } catch { } }
    }

    if ($typeIndex.Count -eq 0) {
        if ($ioErrors.Count -gt 0) { return $ioErrors }
        return @("ENT000: No assemblies found to validate entrypoints. Expected outputs under '$OutputDir'.")
    }

    $errors = @()
    foreach ($ep in $EntryPoints) {
        $impl = $ep.implementation
        if (-not $impl) { continue }

        if (-not $typeIndex.ContainsKey([string]$impl)) {
            $errors += "ENT001: entryPoints implementation type '$impl' was not found in built assemblies under '$OutputDir'."
        }
        elseif (-not $typeIndex[[string]$impl]) {
            $errors += "ENT002: entryPoints implementation type '$impl' exists but is not public; plugin loaders commonly require public entrypoints."
        }
    }

    return $errors
}

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

if ($ValidateEntryPoints -and $manifest.entryPoints) {
    $resolved = Resolve-EntryPointAssemblyPaths -Manifest $manifest -Project $project -NsMgr $nsmgr -ProjectPath $ProjectPath -Configuration $Configuration -TargetFramework $TargetFramework
    $epErrors = Test-EntryPointTypesExist -OutputDir $resolved.OutputDir -AssemblyPaths $resolved.AssemblyPaths -EntryPoints $manifest.entryPoints
    foreach ($msg in $epErrors) { $errorList += $msg }
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
