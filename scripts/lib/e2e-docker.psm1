# E2E Docker helpers: deterministic, timeout-bounded Docker availability checks and safe diagnostics.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Optional dependency: e2e-diagnostics provides Invoke-ValueRedaction for RFC1918/IPv6 redaction.
$diagnosticsPath = Join-Path $PSScriptRoot "e2e-diagnostics.psm1"
if (Test-Path $diagnosticsPath) {
    Import-Module $diagnosticsPath -Force
}

function Get-E2EDockerSafeStderr {
    param(
        [string]$Stderr,
        [int]$MaxLength = 400
    )

    if ([string]::IsNullOrWhiteSpace($Stderr)) { return $null }

    $text = $Stderr -replace '\s+', ' '
    $text = $text.Trim()

    if (Get-Command Invoke-ValueRedaction -ErrorAction SilentlyContinue) {
        try { $text = Invoke-ValueRedaction -Value $text } catch { }
    }

    if ($text.Length -gt $MaxLength) {
        return ($text.Substring(0, $MaxLength) + '...[TRUNCATED]')
    }

    return $text
}

function Get-E2EDockerFailureKind {
    <#
    .SYNOPSIS
        Classify docker failures into stable kinds for E2E_DOCKER_UNAVAILABLE.
    #>
    param(
        [string]$Stderr,
        [int]$ExitCode,
        [switch]$TimedOut
    )

    if ($TimedOut) { return 'daemon_unavailable' }
    if ([string]::IsNullOrWhiteSpace($Stderr)) { return 'unknown' }

    $msg = ($Stderr -replace '\s+', ' ').Trim().ToLowerInvariant()

    # Note: use `\.` (not `\\.`) to match literal dots; `\\.` would match a backslash + any char.
    if ($msg -match 'permission denied' -and ($msg -match 'docker\.sock' -or $msg -match 'docker_engine' -or $msg -match '/var/run/docker\.sock' -or $msg -match '\\\\\\\\.\\\\pipe\\\\\\\\docker_engine')) {
        return 'permission_denied'
    }

    if ($msg -match 'cannot connect to the docker daemon' -or
        $msg -match 'error during connect' -or
        $msg -match 'is the docker daemon running' -or
        $msg -match 'context deadline exceeded' -or
        $msg -match 'tls handshake timeout' -or
        $msg -match 'x509' -or
        $msg -match '/var/run/docker\.sock' -or
        $msg -match '\\\\\\\\.\\\\pipe\\\\\\\\docker_engine' -or
        $msg -match 'open //\\./pipe/docker_engine' -or
        $msg -match 'dial unix' -or
        $msg -match 'connect: connection refused') {
        return 'daemon_unavailable'
    }

    # Keep container-not-found detection narrow; generic "container ... not found" matches exec errors ("container process ... not found").
    if ($msg -match 'no such container' -or $msg -match 'no such object') {
        return 'container_not_found'
    }

    if ($msg -match 'oci runtime exec failed' -or $msg -match 'executable file not found in \\$path' -or $msg -match 'unable to start container process') {
        return 'exec_failed'
    }

    return 'unknown'
}

function Get-E2EDockerFailureSuggestion {
    param([string]$FailureKind)

    switch ($FailureKind) {
        'cli_missing' { return 'Install Docker and ensure the docker CLI is on PATH.' }
        'daemon_unavailable' { return 'Start Docker Desktop / dockerd and verify `docker ps` succeeds. Check your Docker context and TLS settings.' }
        'permission_denied' { return 'Fix Docker permissions (e.g., add user to docker group / use sudo) and ensure access to the Docker socket.' }
        'container_not_found' { return 'Verify the container name is correct and the container is running.' }
        'exec_failed' { return 'Container exec failed. Verify required tools exist in the container and the container is healthy.' }
        default { return 'Verify Docker is installed and running, and that the container is accessible.' }
    }
}

function Invoke-E2EDocker {
    <#
    .SYNOPSIS
        Runs a docker CLI command with a timeout and captures stdout/stderr.
    #>
    param(
        [Parameter(Mandatory)]
        [string[]]$Args,
        [int]$TimeoutSec = 5
    )

    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        return [PSCustomObject]@{
            Success = $false
            ExitCode = 127
            StdOut = ''
            StdErr = "docker CLI not found"
            TimedOut = $false
        }
    }

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = 'docker'
    foreach ($arg in $Args) { [void]$psi.ArgumentList.Add($arg) }
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $proc = [System.Diagnostics.Process]::new()
    $proc.StartInfo = $psi

    $null = $proc.Start()

    $timedOut = -not $proc.WaitForExit([Math]::Max(1, $TimeoutSec) * 1000)
    if ($timedOut) {
        try { $proc.Kill($true) } catch { }
    }

    $stdout = ''
    $stderr = ''
    try { $stdout = $proc.StandardOutput.ReadToEnd() } catch { }
    try { $stderr = $proc.StandardError.ReadToEnd() } catch { }

    return [PSCustomObject]@{
        Success = (-not $timedOut) -and ($proc.ExitCode -eq 0)
        ExitCode = if ($timedOut) { 124 } else { $proc.ExitCode }
        StdOut = $stdout
        StdErr = $stderr
        TimedOut = $timedOut
    }
}

function New-DockerUnavailableDetails {
    <#
    .SYNOPSIS
        Builds structured details for E2E_DOCKER_UNAVAILABLE without leaking private endpoints.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Phase,
        [Parameter(Mandatory)]
        [string]$Operation,
        [string]$ContainerName = $null,
        [int]$DockerExitCode = 0,
        [string]$DockerStderr = $null,
        [switch]$TimedOut
    )

    $kind = Get-E2EDockerFailureKind -Stderr $DockerStderr -ExitCode $DockerExitCode -TimedOut:$TimedOut
    if ($kind -eq 'unknown' -and (-not (Get-Command docker -ErrorAction SilentlyContinue))) {
        $kind = 'cli_missing'
    }

    $dockerPhase = switch ($kind) {
        'cli_missing' { 'Docker:DetectCli' }
        'daemon_unavailable' { 'Docker:DetectDaemon' }
        'permission_denied' { 'Docker:Permission' }
        default { 'Docker:Unknown' }
    }

    $safeStderr = Get-E2EDockerSafeStderr -Stderr $DockerStderr
    $suggestion = Get-E2EDockerFailureSuggestion -FailureKind $kind

    return @{
        ErrorCode = 'E2E_DOCKER_UNAVAILABLE'
        Phase = $Phase
        Operation = $Operation
        ContainerName = $ContainerName
        DockerPhase = $dockerPhase
        DockerFailureKind = $kind
        DockerExitCode = $DockerExitCode
        DockerStderr = $safeStderr
        Suggestion = $suggestion
    }
}

function Test-E2EDockerAvailable {
    <#
    .SYNOPSIS
        Checks docker CLI + daemon availability for E2E gates.
    .OUTPUTS
        PSCustomObject { Success; ErrorCode?; Details? }
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Phase,
        [Parameter(Mandatory)]
        [string]$Operation,
        [int]$TimeoutSec = 5
    )

    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        return [PSCustomObject]@{
            Success = $false
            ErrorCode = 'E2E_DOCKER_UNAVAILABLE'
            Details = (New-DockerUnavailableDetails -Phase $Phase -Operation $Operation -DockerExitCode 127 -DockerStderr "docker CLI not found")
        }
    }

    $probe = Invoke-E2EDocker -Args @('ps') -TimeoutSec $TimeoutSec
    if ($probe.Success) {
        return [PSCustomObject]@{ Success = $true }
    }

    return [PSCustomObject]@{
        Success = $false
        ErrorCode = 'E2E_DOCKER_UNAVAILABLE'
        Details = (New-DockerUnavailableDetails -Phase $Phase -Operation $Operation -DockerExitCode $probe.ExitCode -DockerStderr $probe.StdErr -TimedOut:$probe.TimedOut)
    }
}

Export-ModuleMember -Function Invoke-E2EDocker, Test-E2EDockerAvailable, New-DockerUnavailableDetails
