$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot '..\\lib\\e2e-docker.psm1') -Force

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "E2E Docker Unavailable Tests" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$failed = 0
$passed = 0

function Assert-True {
    param([string]$Name, [bool]$Condition)
    if (-not $Condition) {
        Write-Host "  [FAIL] $Name" -ForegroundColor Red
        $script:failed++
    } else {
        Write-Host "  [PASS] $Name" -ForegroundColor Green
        $script:passed++
    }
}

function Assert-Equal {
    param([string]$Name, $Actual, $Expected)
    if ($Actual -ne $Expected) {
        Write-Host "  [FAIL] $Name (expected=$Expected actual=$Actual)" -ForegroundColor Red
        $script:failed++
    } else {
        Write-Host "  [PASS] $Name" -ForegroundColor Green
        $script:passed++
    }
}

# Daemon unavailable classification
$daemon = New-DockerUnavailableDetails `
    -Phase 'Grab:FileValidation' `
    -Operation 'docker exec' `
    -ContainerName 'lidarr-e2e-test' `
    -DockerExitCode 1 `
    -DockerStderr 'Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?'

Assert-Equal -Name "daemon unavailable -> ErrorCode" -Actual $daemon.ErrorCode -Expected 'E2E_DOCKER_UNAVAILABLE'
Assert-Equal -Name "daemon unavailable -> DockerFailureKind" -Actual $daemon.DockerFailureKind -Expected 'daemon_unavailable'
Assert-Equal -Name "daemon unavailable -> DockerPhase" -Actual $daemon.DockerPhase -Expected 'Docker:DetectDaemon'
Assert-True -Name "daemon unavailable -> Suggestion mentions docker ps" -Condition ($daemon.Suggestion -match 'docker ps')

# Permission denied classification (socket access)
$perm = New-DockerUnavailableDetails `
    -Phase 'Persist:Prereq' `
    -Operation 'docker restart' `
    -ContainerName 'lidarr-e2e-test' `
    -DockerExitCode 1 `
    -DockerStderr 'permission denied while trying to connect to the Docker daemon socket at unix:///var/run/docker.sock'

Assert-Equal -Name "permission denied -> DockerFailureKind" -Actual $perm.DockerFailureKind -Expected 'permission_denied'
Assert-Equal -Name "permission denied -> DockerPhase" -Actual $perm.DockerPhase -Expected 'Docker:Permission'

# Container not found classification
$missingContainer = New-DockerUnavailableDetails `
    -Phase 'Metadata:InspectContainer' `
    -Operation 'docker inspect' `
    -ContainerName 'does-not-exist' `
    -DockerExitCode 1 `
    -DockerStderr 'Error: No such container: does-not-exist'

Assert-Equal -Name "container not found -> DockerFailureKind" -Actual $missingContainer.DockerFailureKind -Expected 'container_not_found'
Assert-True -Name "container not found -> Suggestion mentions container name" -Condition ($missingContainer.Suggestion -match 'container')

# Exec failed classification (tool missing)
$execFailed = New-DockerUnavailableDetails `
    -Phase 'Grab:ValidateOutputPath' `
    -Operation 'docker exec' `
    -ContainerName 'lidarr-e2e-test' `
    -DockerExitCode 1 `
    -DockerStderr 'OCI runtime exec failed: exec failed: unable to start container process: exec: \"python3\": executable file not found in $PATH: unknown'

Assert-Equal -Name "exec failed -> DockerFailureKind" -Actual $execFailed.DockerFailureKind -Expected 'exec_failed'

# Redaction: RFC1918 + secret query params must not survive in DockerStderr
$redactionInput = 'http://192.168.1.100:11434/v1/models?access_token=abc123&albumId=42'
$redacted = New-DockerUnavailableDetails `
    -Phase 'BrainarrLLM:DetectModels' `
    -Operation 'docker exec' `
    -DockerExitCode 1 `
    -DockerStderr $redactionInput

Assert-True -Name "redaction -> private IP redacted" -Condition ($redacted.DockerStderr -notmatch '192\.168\.' -and $redacted.DockerStderr -match '\[PRIVATE-IP\]')
Assert-True -Name "redaction -> access_token redacted" -Condition ($redacted.DockerStderr -notmatch 'access_token=abc123' -and $redacted.DockerStderr -match 'access_token=\[REDACTED\]')
Assert-True -Name "redaction -> non-sensitive albumId preserved" -Condition ($redacted.DockerStderr -match 'albumId=42')

# Truncation: very long stderr should be truncated deterministically
$long = ('x' * 600) + ' http://10.0.0.1:1234?token=secret'
$truncated = New-DockerUnavailableDetails -Phase 'X' -Operation 'docker ps' -DockerExitCode 1 -DockerStderr $long
Assert-True -Name "truncation -> adds [TRUNCATED]" -Condition ($truncated.DockerStderr -match '\[TRUNCATED\]')
Assert-True -Name "truncation -> length bounded" -Condition ($truncated.DockerStderr.Length -le 430)

Write-Host ""
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor Red

if ($failed -gt 0) {
    throw "E2E docker unavailable tests failed: $failed"
}
