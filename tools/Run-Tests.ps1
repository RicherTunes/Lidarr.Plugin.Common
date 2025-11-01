param(
  [ValidateSet('Debug','Release')] [string]$Configuration = 'Release',
  [switch]$IncludeCLIFramework,
  [string]$Filter = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Resolve-Path (Join-Path $root '..')
Push-Location $repo
try {
  $args = @('test', '-c', $Configuration, '--no-build', '--settings', (Join-Path $repo 'test.runsettings'))
  if (-not [string]::IsNullOrWhiteSpace($Filter)) { $args += @('--filter', $Filter) }
  if ($IncludeCLIFramework) { $args += @('-p:IncludeCLIFramework=true') }

  # Build once; re-use binaries across test invocations
  & dotnet build -c $Configuration | Write-Host
  & dotnet @args | Write-Host
}
finally { Pop-Location }
