# Testing with the TestKit

`Lidarr.Plugin.Common.TestKit` (ships with the repository today; NuGet publishing planned for 1.2) provides fixtures, HTTP handlers, and manifest helpers so plugins can verify their implementations without copy/paste harnesses.

## AssemblyLoadContext harness

Use the isolation host sample to load plugins into collectible contexts, assert metadata, and unload cleanly. See [PLUGIN_ISOLATION.md](PLUGIN_ISOLATION.md) for the loader snippet.

## HTTP handlers & gzip safety net

```csharp file=../tests/HttpClientExtensionsTests.cs#guard-stream
```

```csharp file=../tests/HttpClientExtensionsTests.cs#sniffer-passthrough
```

Guard streams confirm that `ContentDecodingSnifferHandler` only peeks the gzip header and preserves `Content-Length` for pass-through payloads.

## Resilience and cancellation tests

```csharp file=../tests/HttpClientExtensionsTests.cs#resilience-cancel
```

```csharp file=../tests/GenericResilienceExecutorTests.cs#generic-cancel
```

These tests assert that per-request timeouts raise `TimeoutException` while caller cancellations propagate as `TaskCanceledException`/`OperationCanceledException` without retrying.

## Using the TestKit

1. Reference the project (or future NuGet) in your plugin test project.
2. Use the HTTP handlers (gzip mislabel, flaky 429, partial content, slow stream) to exercise resilience paths.
3. Instantiate the ALC fixture to load your plugin directly from disk for end-to-end integration tests.
4. Run `dotnet test` locally; CI executes the same suite.

## Test Category Contract

All plugins in the ecosystem MUST use these standard xUnit trait categories:

| Category | Description | When to Run | CI Job |
|----------|-------------|-------------|--------|
| `Integration` | Tests requiring external services (APIs, network) | Manual/Nightly | Optional |
| `Packaging` | Tests validating ILRepack merged assemblies | After `dotnet build -c Release` | Required for packaging changes |
| `LibraryLinking` | Tests verifying assembly isolation and DI | After packaging | Required for packaging changes |
| `Benchmark` | Performance/timing tests (non-deterministic) | Manual/Opt-in | Never in CI |
| `Slow` | Tests taking >5s or with timing dependencies | Opt-in | Exclude by default |

### Usage

```csharp
[Fact]
[Trait("Category", "Packaging")]
public void MergedAssembly_Should_InternalizeCommonTypes()
{
    // Test ILRepack internalization
}

[Fact]
[Trait("Category", "Slow")]
public void LargeAlbum_Download_Completes_Within_Timeout()
{
    // Timing-sensitive test
}
```

### Test Runner Scripts

Each plugin MUST provide two test scripts:

1. **`scripts/test.ps1`** - Unit tests (unmerged mode)
   - Excludes: `Packaging`, `LibraryLinking`, `Benchmark`, `Slow`
   - Runs on every PR

2. **`scripts/test-packaging.ps1`** - Packaging tests (merged mode)
   - Includes: `Packaging`, `LibraryLinking`
   - Requires: `-RequirePackage` flag for CI
   - Validates artifact freshness (version matches checkout)

### Centralized Assertions

Use the TestKit assertion helpers to prevent test drift:

```csharp
// Log security - ensures no secrets leak
LogAssertions.AssertNoSecretsInLogs(sink, apiKey, token);

// URL security - ensures no query params leak (uses default safe hosts)
LogAssertions.AssertNoUnredactedUrls(sink);

// URL security with custom safe hosts (for integration tests with test servers)
LogAssertions.AssertNoUnredactedUrls(sink, safeHosts: new[] { "my-test-server.local" });

// All-in-one security check
LogAssertions.AssertSecureLogs(sink, apiKey, token);

// ILRepack internalization - ensures proper packaging
PluginIsolationAssertions.AssertNoPublicCommonTypesInMergedAssembly(assembly);

// Manifest consistency - ensures plugin.json matches manifest.json
PluginIsolationAssertions.AssertManifestConsistency(pluginDir);

// File naming - ensures Unicode NFC normalization
FileNameAssertions.AssertNormalizedToFormC(fileName);
```

### Default Safe Hosts

`LogAssertions.AssertNoUnredactedUrls` automatically excludes these hosts from URL checks:
- `localhost`, `127.0.0.1`, `::1`, `[::1]`, `0.0.0.0`
- `host.docker.internal`, `testserver`, `test.local`
- `example.com`, `example.org`, `example.net`

This prevents false positives when testing against local or mock services. Pass an empty array to check all URLs or a custom list to override.

## Shared PowerShell Test Runner Module

All plugins should use the shared test runner module from Common to ensure consistent behavior:

**Location**: `ext/Lidarr.Plugin.Common/scripts/lib/test-runner.psm1`

### Available Functions

| Function | Purpose |
|----------|---------|
| `Get-TrxTestSummary` | Parse TRX files (uses `notExecuted` for skipped, not `inconclusive`) |
| `Write-TestSummary` | Display formatted test results |
| `Test-ArtifactFreshness` | Validate package version/SHA matches checkout (MSBuild query) |
| `Find-PluginAssembly` | Locate merged plugin DLL (case-insensitive, multiple search paths) |
| `Expand-PluginPackage` | Extract zip to temp directory |
| `Remove-ExtractedPackage` | Cleanup temp extraction directory |
| `Get-StandardBuildArgs` | Generate build args with packaging flags |
| `Get-StandardTestArgs` | Generate test args with category filtering |
| `Get-PackagingTestArgs` | Generate args for Packaging/LibraryLinking tests |

### Usage Example

```powershell
# In your plugin's scripts/test.ps1
$CommonScripts = Join-Path $PSScriptRoot "../../ext/Lidarr.Plugin.Common/scripts/lib"
Import-Module (Join-Path $CommonScripts "test-runner.psm1") -Force

# Use shared functions
$buildArgs = Get-StandardBuildArgs -TestProject $TestProject -Configuration $Configuration
& dotnet @buildArgs

$testArgs = Get-StandardTestArgs -TestProject $TestProject -OutputDir $OutputDir -TrxFileName "MyPlugin.Tests.trx"
& dotnet @testArgs

$summary = Get-TrxTestSummary -TrxPath (Join-Path $OutputDir "MyPlugin.Tests.trx")
if ($summary) { Write-TestSummary -Summary $summary }
```

### Migration Path

Existing plugin scripts can gradually adopt the module:
1. Import the module at script start
2. Replace TRX parsing with `Get-TrxTestSummary`
3. Replace freshness checks with `Test-ArtifactFreshness`
4. Replace assembly discovery with `Find-PluginAssembly`

## General Contract

- Every plugin test project should validate gzip sniffing, resilience retries, manifest gating, and ALC unload behaviour.
- Use the shared handlers/fixtures instead of bespoke mocks to reduce drift.
- Keep tests deterministic (no real network); use the provided handlers or handcrafted `DelegatingHandler`s.
- When adding new handlers or fixtures, update this page and tag snippets so the verifier keeps docs honest.
