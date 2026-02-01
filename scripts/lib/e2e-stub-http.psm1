# e2e-stub-http.psm1 - Stub HTTP server infrastructure for hermetic E2E testing
# Enables PR-safe E2E tests without requiring real service credentials

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Default stub responses for common API scenarios
$script:StubResponses = @{
    # Qobuz API stubs
    "qobuz_auth_success" = @{
        StatusCode = 200
        Body = @{
            user_auth_token = "stub_auth_token_12345"
            user = @{
                id = 12345678
                login = "stub@test.local"
                country_code = "US"
            }
        } | ConvertTo-Json -Compress
    }
    "qobuz_auth_401" = @{
        StatusCode = 401
        Body = @{
            status = "error"
            code = 401
            message = "Invalid credentials"
        } | ConvertTo-Json -Compress
    }
    "qobuz_search_success" = @{
        StatusCode = 200
        Body = @{
            albums = @{
                items = @(
                    @{
                        id = "stub_album_001"
                        title = "Stub Album - Kind of Blue"
                        artist = @{ name = "Stub Artist - Miles Davis" }
                        tracks_count = 5
                    }
                )
                total = 1
            }
        } | ConvertTo-Json -Depth 10 -Compress
    }
    "qobuz_429" = @{
        StatusCode = 429
        Headers = @{ "Retry-After" = "60" }
        Body = @{
            status = "error"
            code = 429
            message = "Too many requests"
        } | ConvertTo-Json -Compress
    }

    # Tidal API stubs
    "tidal_auth_success" = @{
        StatusCode = 200
        Body = @{
            access_token = "stub_tidal_token"
            token_type = "Bearer"
            expires_in = 3600
            user = @{
                userId = 87654321
                countryCode = "US"
            }
        } | ConvertTo-Json -Compress
    }
    "tidal_auth_401" = @{
        StatusCode = 401
        Body = @{
            status = 401
            subStatus = 6001
            userMessage = "Invalid credentials"
        } | ConvertTo-Json -Compress
    }

    # Generic stubs
    "generic_500" = @{
        StatusCode = 500
        Body = @{
            error = "Internal Server Error"
            message = "Something went wrong"
        } | ConvertTo-Json -Compress
    }
    "generic_malformed_json" = @{
        StatusCode = 200
        Body = '{"broken": json, not valid'
    }
}

<#
.SYNOPSIS
    Gets a stub response preset by name.
.PARAMETER Name
    Name of the stub response preset.
.OUTPUTS
    Hashtable with StatusCode, Headers, and Body properties.
#>
function Get-StubResponse {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($script:StubResponses.ContainsKey($Name)) {
        return $script:StubResponses[$Name]
    }

    throw "Unknown stub response preset: $Name"
}

<#
.SYNOPSIS
    Starts a stub HTTP server in a Docker container for hermetic E2E testing.
.DESCRIPTION
    Creates a simple HTTP server container that returns predetermined responses.
    The container exposes port 8080 internally and maps to the specified host port.
.PARAMETER ContainerName
    Name for the stub server container.
.PARAMETER HostPort
    Host port to bind the stub server to.
.PARAMETER Routes
    Hashtable mapping URL paths to stub response names or custom responses.
    Example: @{ "/v1/auth" = "qobuz_auth_success"; "/v1/search" = "qobuz_search_success" }
.OUTPUTS
    PSCustomObject with Success, ContainerName, Port, and Error properties.
#>
function Start-StubHttpServer {
    param(
        [string]$ContainerName = "e2e-stub-http",

        [int]$HostPort = 8888,

        [hashtable]$Routes = @{}
    )

    $result = [PSCustomObject]@{
        Success = $false
        ContainerName = $ContainerName
        Port = $HostPort
        BaseUrl = "http://localhost:$HostPort"
        Error = $null
    }

    try {
        # Stop existing container if running
        & docker rm -f $ContainerName 2>$null | Out-Null

        # Build route configuration
        $routeConfig = @{}
        foreach ($path in $Routes.Keys) {
            $responseSpec = $Routes[$path]
            if ($responseSpec -is [string]) {
                # It's a preset name
                $routeConfig[$path] = Get-StubResponse -Name $responseSpec
            }
            elseif ($responseSpec -is [hashtable]) {
                # It's a custom response
                $routeConfig[$path] = $responseSpec
            }
        }

        # Encode route config as JSON and escape for shell
        $routeJson = $routeConfig | ConvertTo-Json -Depth 10 -Compress
        # Base64 encode to avoid shell quoting issues
        $routeJsonBytes = [System.Text.Encoding]::UTF8.GetBytes($routeJson)
        $routeJsonBase64 = [Convert]::ToBase64String($routeJsonBytes)

        # Modify Python script to decode base64
        $pythonScriptWithDecode = @"
import http.server
import json
import sys
import base64

ROUTES = json.loads(base64.b64decode(sys.argv[1]).decode('utf-8'))

class StubHandler(http.server.BaseHTTPRequestHandler):
    def log_message(self, format, *args):
        print(f"[STUB] {args[0]}")

    def do_GET(self):
        self._handle_request()

    def do_POST(self):
        self._handle_request()

    def _handle_request(self):
        path = self.path.split('?')[0]
        if path in ROUTES:
            route = ROUTES[path]
            status = route.get('StatusCode', 200)
            headers = route.get('Headers', {})
            body = route.get('Body', '{}')

            self.send_response(status)
            self.send_header('Content-Type', 'application/json')
            for k, v in headers.items():
                self.send_header(k, v)
            self.end_headers()
            self.wfile.write(body.encode('utf-8'))
        else:
            self.send_response(404)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(json.dumps({"error": "Not found", "path": path}).encode('utf-8'))

server = http.server.HTTPServer(('0.0.0.0', 8080), StubHandler)
print(f"[STUB] Server listening on port 8080")
server.serve_forever()
"@

        # Start container with Python server
        $dockerArgs = @(
            "run", "-d",
            "--name", $ContainerName,
            "-p", "${HostPort}:8080",
            "python:3.11-slim",
            "python", "-c", $pythonScriptWithDecode, $routeJsonBase64
        )

        $containerId = & docker @dockerArgs 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to start stub server: $containerId"
        }

        # Wait for server to be ready
        $start = Get-Date
        $ready = $false
        while (((Get-Date) - $start).TotalSeconds -lt 30) {
            try {
                $null = Invoke-WebRequest -Uri "http://localhost:$HostPort/" -TimeoutSec 2 -ErrorAction SilentlyContinue
                $ready = $true
                break
            }
            catch {
                if ($_.Exception.Response.StatusCode -eq 404) {
                    # 404 means server is responding
                    $ready = $true
                    break
                }
                Start-Sleep -Milliseconds 500
            }
        }

        if (-not $ready) {
            $logs = & docker logs $ContainerName 2>&1
            throw "Stub server did not become ready within 30s. Logs: $logs"
        }

        $result.Success = $true
        Write-Host "Stub HTTP server started at http://localhost:$HostPort" -ForegroundColor Green
    }
    catch {
        $result.Error = $_.Exception.Message
        & docker rm -f $ContainerName 2>$null | Out-Null
    }

    return $result
}

<#
.SYNOPSIS
    Stops and removes a stub HTTP server container.
.PARAMETER ContainerName
    Name of the container to stop.
#>
function Stop-StubHttpServer {
    param(
        [string]$ContainerName = "e2e-stub-http"
    )

    & docker rm -f $ContainerName 2>$null | Out-Null
    Write-Host "Stub HTTP server stopped" -ForegroundColor DarkGray
}

<#
.SYNOPSIS
    Gets default stub routes for a specific plugin implementation.
.PARAMETER Implementation
    Plugin implementation name (e.g., "QobuzIndexer").
.PARAMETER Scenario
    Test scenario: "success", "auth_failure", "rate_limit", "malformed"
.OUTPUTS
    Hashtable of path -> response mappings.
#>
function Get-PluginStubRoutes {
    param(
        [Parameter(Mandatory)]
        [string]$Implementation,

        [ValidateSet("success", "auth_failure", "rate_limit", "malformed")]
        [string]$Scenario = "success"
    )

    $routes = @{}

    switch ($Implementation) {
        "QobuzIndexer" {
            switch ($Scenario) {
                "success" {
                    $routes = @{
                        "/user/login" = "qobuz_auth_success"
                        "/album/search" = "qobuz_search_success"
                        "/track/search" = "qobuz_search_success"
                    }
                }
                "auth_failure" {
                    $routes = @{
                        "/user/login" = "qobuz_auth_401"
                    }
                }
                "rate_limit" {
                    $routes = @{
                        "/user/login" = "qobuz_429"
                        "/album/search" = "qobuz_429"
                    }
                }
                "malformed" {
                    $routes = @{
                        "/user/login" = "generic_malformed_json"
                    }
                }
            }
        }
        "TidalLidarrIndexer" {
            switch ($Scenario) {
                "success" {
                    $routes = @{
                        "/v1/oauth2/token" = "tidal_auth_success"
                    }
                }
                "auth_failure" {
                    $routes = @{
                        "/v1/oauth2/token" = "tidal_auth_401"
                    }
                }
            }
        }
    }

    return $routes
}

<#
.SYNOPSIS
    Checks if hermetic/stub mode should be used based on environment and inputs.
.PARAMETER E2EMode
    Explicit mode setting: "hermetic" or "live".
.PARAMETER HasCredentials
    Whether real credentials are available.
.OUTPUTS
    $true if hermetic mode should be used.
#>
function Test-ShouldUseHermeticMode {
    param(
        [string]$E2EMode = "",
        [bool]$HasCredentials = $false
    )

    # Explicit mode setting takes precedence
    if ($E2EMode -eq "hermetic") {
        return $true
    }
    if ($E2EMode -eq "live") {
        return $false
    }

    # Auto-detect: use hermetic if no credentials available
    if (-not $HasCredentials) {
        Write-Host "No credentials detected, using hermetic (stub) mode" -ForegroundColor Yellow
        return $true
    }

    return $false
}

Export-ModuleMember -Function @(
    'Get-StubResponse',
    'Start-StubHttpServer',
    'Stop-StubHttpServer',
    'Get-PluginStubRoutes',
    'Test-ShouldUseHermeticMode'
)
