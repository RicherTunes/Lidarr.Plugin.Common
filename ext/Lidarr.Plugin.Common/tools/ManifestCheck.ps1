# snippet:manifest-ci
# snippet-skip-compile
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,

    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [string]$AbstractionsPackage = 'Lidarr.Plugin.Abstractions',

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

# Resolve Version with namespace-aware query then fallback to no-namespace projects
$versionNode = $project.SelectSingleNode('//msb:Project/msb:PropertyGroup/msb:Version', $nsmgr)
if (-not $versionNode) { $versionNode = $project.SelectSingleNode('//msb:Project/msb:PropertyGroup/msb:AssemblyVersion', $nsmgr) }
if (-not $versionNode) { $versionNode = $project.SelectSingleNode('//Project/PropertyGroup/Version') }
if (-not $versionNode) { $versionNode = $project.SelectSingleNode('//Project/PropertyGroup/AssemblyVersion') }
if (-not $versionNode) {
    throw "Unable to resolve Version from '$ProjectPath'."
}
$projectVersion = $versionNode.InnerText.Trim()

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json

if (-not $manifest.version) {
    throw "Manifest at '$ManifestPath' is missing 'version'."
}

$packageReference = $project.SelectSingleNode("//msb:Project/msb:ItemGroup/msb:PackageReference[@Include='$AbstractionsPackage']", $nsmgr)
if (-not $packageReference) { $packageReference = $project.SelectSingleNode("//Project/ItemGroup/PackageReference[@Include='$AbstractionsPackage']") }
$projectReference = $project.SelectSingleNode("//msb:Project/msb:ItemGroup/msb:ProjectReference[contains(@Include, 'Lidarr.Plugin.Abstractions.csproj') or contains(@Include, 'Abstractions')]", $nsmgr)
if (-not $projectReference) { $projectReference = $project.SelectSingleNode("//Project/ItemGroup/ProjectReference[contains(@Include, 'Lidarr.Plugin.Abstractions.csproj') or contains(@Include, 'Abstractions')]") }

$packageMajor = $null
$packageVersion = $null
$usingProjectRef = $false

if ($packageReference) {
    $packageVersion = $packageReference.Version
    if (-not $packageVersion) {
        throw "PackageReference to $AbstractionsPackage must specify Version."
    }
    $packageMajor = ($packageVersion -split '\.')[0]
}
elseif ($projectReference) {
    # Fall back to manifest.commonVersion for in-repo abstractions; emit MAN001 warning (or error in Strict)
    $usingProjectRef = $true
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

