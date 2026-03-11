# Lidarr.Plugin.Common.TestKit

Shared testing helpers for Lidarr streaming plugins.

## Features

### Plugin Sandbox & Hosting
- `PluginSandbox` fixture that loads a compiled plugin inside a collectible AssemblyLoadContext and disposes it cleanly, making it easy to test side-by-side versions.
- Lightweight `PluginTestContext` with a captured log sink so plugin tests can assert on host logs without real host infrastructure.

### HTTP Test Handlers
- Battle-tested `HttpMessageHandler` shims for the most common upstream behaviours (mislabelled gzip, flaky retries, partial content resumptions, slow streams, preview/sample markers, and problem+json errors).
- `AuthenticationTestHandler` - Simulates OAuth/token authentication flows for testing auth service implementations.
- `RateLimitTestHandler` - Simulates rate limiting responses with configurable windows and limits.
- `ErrorSimulationHandler` - Simulates server errors with configurable failure patterns.

### Test Data Builders
Fluent builders for creating test instances of streaming models:
- `StreamingArtistBuilder` - Create test artists with genres, images, metadata.
- `StreamingAlbumBuilder` - Create test albums with qualities, cover art, external IDs.
- `StreamingTrackBuilder` - Create test tracks with featured artists, ISRCs, qualities.
- `StreamingQualityBuilder` - Create test quality levels (MP3, FLAC CD, Hi-Res, etc.).
- `StreamingSearchResultBuilder` - Create test search results.

### Model Validation
- `ModelValidationHelpers` - Validators for StreamingModels (artist, album, track, quality).
- ISRC format validation.
- UPC/EAN barcode validation with check digit verification.
- Tracklist validation (sequential numbering, no duplicates).
- Quality ordering validation for proper fallback behaviour.

### Compliance Testing
- `PluginComplianceTestBase` - Base class for plugin compliance checks.
- `StreamingServiceComplianceTestBase` - Extended compliance for streaming plugins.
- `SecurityComplianceTestBase` - Security-focused compliance checks.

### Test Data
- Embedded JSON payloads covering tricky streaming metadata (multidisc Qobuz albums, Tidal preview tracks, unicode/emoji artists) for repeatable parsing tests.
- Assertion helpers for download outcomes, quality fallback checks, and preview rejection.

## Getting Started

```xml
<ItemGroup>
  <PackageReference Include="Lidarr.Plugin.Common.TestKit" Version="1.1.8" />
</ItemGroup>
```

Then in your test project:

```csharp
await using var sandbox = await PluginSandbox.CreateAsync(pluginPath);
var indexer = await sandbox.CreateIndexerAsync();
```

### Using Test Data Builders

```csharp
using Lidarr.Plugin.Common.TestKit.Builders;

// Create minimal test data
var artist = StreamingArtistBuilder.CreateMinimal("Pink Floyd");
var album = StreamingAlbumBuilder.CreateLossless("The Dark Side of the Moon");

// Or use the fluent builder for detailed test scenarios
var track = new StreamingTrackBuilder()
    .WithTitle("Money")
    .WithArtist("Pink Floyd")
    .WithTrackNumber(6)
    .WithDuration(TimeSpan.FromMinutes(6.5))
    .WithIsrc("USCA20200001")
    .WithFeaturedArtist("Guest Artist")
    .WithQuality(StreamingQualityBuilder.CreateFlacHiRes())
    .Build();
```

### Using Model Validation

```csharp
using Lidarr.Plugin.Common.TestKit.Validation;

var result = ModelValidationHelpers.ValidateAlbum(album, requireComplete: true);
if (!result.IsValid)
{
    Console.WriteLine(result.GetErrorSummary());
}

// Validate ISRC/UPC codes
ModelValidationHelpers.ValidateIsrc("USCA20200001").ThrowIfInvalid();
ModelValidationHelpers.ValidateUpc("012345678905").ThrowIfInvalid();
```

### Using HTTP Test Handlers

```csharp
using Lidarr.Plugin.Common.TestKit.Http;

// Simulate authentication flows
var authHandler = new AuthenticationTestHandler(new AuthenticationTestOptions
{
    TokenLifetime = TimeSpan.FromMinutes(5),
    SimulateRefreshFailure = false
});

// Simulate rate limiting
var rateLimitHandler = new RateLimitTestHandler(new RateLimitTestOptions
{
    RequestsPerWindow = 10,
    WindowDuration = TimeSpan.FromSeconds(30)
});

// Simulate server errors
var errorHandler = new ErrorSimulationHandler(new ErrorSimulationOptions
{
    FailOnRequestNumber = 3, // Fail on the 3rd request
    ErrorStatusCode = HttpStatusCode.ServiceUnavailable
});
```

See the library documentation under `docs/how-to/TEST_WITH_TESTKIT.md` for full guidance.
