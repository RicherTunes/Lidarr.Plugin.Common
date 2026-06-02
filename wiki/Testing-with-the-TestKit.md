# Testing with the TestKit

The TestKit (`Lidarr.Plugin.Common.TestKit` NuGet package) ships reusable test infrastructure so every plugin tests the same way.
This page is a **catalog** of the fixtures, harnesses, HTTP handlers, builders, and assertion helpers it exposes — with source deep-links.

For full usage instructions, see [TESTING_WITH_TESTKIT.md](../docs/TESTING_WITH_TESTKIT.md); this page does not restate it.

## HTTP Handlers

Simulate upstream API behaviour in unit tests by inserting these `HttpMessageHandler` / `DelegatingHandler` implementations into your `HttpClient` pipeline.

| Type | Purpose | Source |
|------|---------|--------|
| `AuthenticationTestHandler` | Simulates OAuth/token authentication flows. | [AuthenticationTestHandler.cs](../testkit/Http/AuthenticationTestHandler.cs) |
| `AuthenticationTestOptions` | Configures `AuthenticationTestHandler` behaviour. | [AuthenticationTestHandler.cs](../testkit/Http/AuthenticationTestHandler.cs) |
| `RateLimitTestHandler` | Simulates rate-limiting responses. | [AuthenticationTestHandler.cs](../testkit/Http/AuthenticationTestHandler.cs) |
| `RateLimitTestOptions` | Configures `RateLimitTestHandler`. | [AuthenticationTestHandler.cs](../testkit/Http/AuthenticationTestHandler.cs) |
| `ErrorSimulationHandler` | Simulates server errors with configurable patterns. | [AuthenticationTestHandler.cs](../testkit/Http/AuthenticationTestHandler.cs) |
| `ErrorSimulationOptions` | Configures `ErrorSimulationHandler`. | [AuthenticationTestHandler.cs](../testkit/Http/AuthenticationTestHandler.cs) |
| `SlowStreamHandler` | Simulates slow streaming responses. | [SlowStreamHandler.cs](../testkit/Http/SlowStreamHandler.cs) |
| `PreviewStreamHandler` | Serves preview/partial stream responses. | [PreviewStreamHandler.cs](../testkit/Http/PreviewStreamHandler.cs) |
| `RetriableFlakyHandler` | Simulates intermittent failures for retry testing. | [RetriableFlakyHandler.cs](../testkit/Http/RetriableFlakyHandler.cs) |
| `PartialContentHandler` | Simulates HTTP 206 Partial Content responses. | [PartialContentHandler.cs](../testkit/Http/PartialContentHandler.cs) |
| `GzipMislabeledHandler` | Simulates mislabeled gzip content-encoding. | [GzipMislabeledHandler.cs](../testkit/Http/GzipMislabeledHandler.cs) |
| `JsonProblemHandler` | Simulates RFC 7807 problem-detail responses. | [JsonProblemHandler.cs](../testkit/Http/JsonProblemHandler.cs) |
| `HttpResponseFactory` | Static factory for crafting test HTTP responses. | [HttpResponseFactory.cs](../testkit/Http/HttpResponseFactory.cs) |

## Hosting and Container Fixtures

Spin up a real Lidarr container or a lightweight in-memory plugin context.

| Type | Purpose | Source |
|------|---------|--------|
| `LidarrContainerFixture` | xUnit collection fixture that boots a real Lidarr container with a plugin DLL. | [LidarrContainerFixture.cs](../testkit/Hosting/LidarrContainerFixture.cs) |
| `LidarrContainerOptions` | Record configuring which image, tag, and plugin to load. | [LidarrContainerOptions.cs](../testkit/Hosting/LidarrContainerOptions.cs) |
| `LidarrContainerFixtureSmokeAssertions` | Static assertions for smoke-testing the running container. | [LidarrContainerFixtureSmokeAssertions.cs](../testkit/Hosting/LidarrContainerFixtureSmokeAssertions.cs) |
| `PluginTestContext` | Minimal `IPluginContext` implementation for unit tests. | [PluginTestContext.cs](../testkit/Hosting/PluginTestContext.cs) |
| `TestLogSink` | Captures log entries emitted during a test. | [PluginTestContext.cs](../testkit/Hosting/PluginTestContext.cs) |
| `TestLogEntry` | Single structured log entry record. | [PluginTestContext.cs](../testkit/Hosting/PluginTestContext.cs) |
| `TestLoggerFactory` | Injectable `ILoggerFactory` backed by the test sink. | [PluginTestContext.cs](../testkit/Hosting/PluginTestContext.cs) |

## Compliance and Contract Bases

Abstract base classes that enforce ecosystem-wide rules. Inherit them in your plugin's test project.

| Type | Purpose | Source |
|------|---------|--------|
| `PluginComplianceTestBase` | Abstract base for compliance tests every plugin must pass. | [PluginComplianceTestBase.cs](../testkit/Compliance/PluginComplianceTestBase.cs) |
| `StreamingServiceComplianceTestBase` | Compliance tests specific to streaming services (extends `PluginComplianceTestBase`). | [StreamingServiceComplianceTestBase.cs](../testkit/Compliance/StreamingServiceComplianceTestBase.cs) |
| `SecurityComplianceTestBase` | Security-focused compliance tests for all plugins. | [SecurityComplianceTestBase.cs](../testkit/Compliance/SecurityComplianceTestBase.cs) |
| `EcosystemParityTestBase` | Abstract base for ecosystem parity tests (file/class naming, shared-type usage). | [EcosystemParityTestBase.cs](../testkit/Compliance/EcosystemParityTestBase.cs) |
| `DiagnosticsAllowedValuesTestBase` | Validates that diagnostics expose only allowed values. | [DiagnosticsAllowedValuesTestBase.cs](../testkit/Compliance/DiagnosticsAllowedValuesTestBase.cs) |
| `PluginVersionContract` | Static contract assertions for plugin versioning. | [PluginVersionContract.cs](../testkit/Compliance/PluginVersionContract.cs) |
| `PublishedReleaseInstallabilityContract` | Static contract for release installability. | [PublishedReleaseInstallabilityContract.cs](../testkit/Compliance/PublishedReleaseInstallabilityContract.cs) |
| `PluginPackagingContract` | Static contract for packaging compliance; references `PluginPackagePolicy`. | [PluginPackagingContract.cs](../testkit/Compliance/PluginPackagingContract.cs) |

## Isolation and ALC Harness

Verify that your plugin loads correctly inside a dedicated AssemblyLoadContext.

| Type | Purpose | Source |
|------|---------|--------|
| `PluginIsolationTestBase` | Abstract base for ALC isolation tests. | [PluginIsolationTestBase.cs](../testkit/LibraryLinking/PluginIsolationTestBase.cs) |
| `StreamingPluginIsolationTestBase` | ALC tests specific to streaming plugins (extends `PluginIsolationTestBase`). | [PluginIsolationTestBase.cs](../testkit/LibraryLinking/PluginIsolationTestBase.cs) |
| `PluginIsolationAssertions` | Static assertions for isolation and manifest consistency. | [PluginIsolationAssertions.cs](../testkit/LibraryLinking/PluginIsolationAssertions.cs) |
| `PluginSandbox` | Isolated sandbox that loads a plugin assembly in a separate ALC (`IAsyncDisposable`). | [PluginSandbox.cs](../testkit/Fixtures/PluginSandbox.cs) |
| `SandboxLoaderMode` | Controls how `PluginSandbox` loads the plugin assembly. | [PluginSandbox.cs](../testkit/Fixtures/PluginSandbox.cs) |
| `PluginSandboxOptions` | Configures sandbox behaviour. | [PluginSandbox.cs](../testkit/Fixtures/PluginSandbox.cs) |
| `BridgeComplianceFixture` | Fixture verifying bridge compliance (`IDisposable`). | [BridgeComplianceFixture.cs](../testkit/Fixtures/BridgeComplianceFixture.cs) |

## Builders

Fluent builders for constructing streaming model instances in tests.

| Type | Purpose | Source |
|------|---------|--------|
| `StreamingArtistBuilder` | Fluent builder for `StreamingArtist` instances. | [StreamingModelBuilders.cs](../testkit/Builders/StreamingModelBuilders.cs) |
| `StreamingAlbumBuilder` | Fluent builder for `StreamingAlbum` instances. | [StreamingModelBuilders.cs](../testkit/Builders/StreamingModelBuilders.cs) |
| `StreamingTrackBuilder` | Fluent builder for `StreamingTrack` instances. | [StreamingModelBuilders.cs](../testkit/Builders/StreamingModelBuilders.cs) |
| `StreamingQualityBuilder` | Fluent builder for `StreamingQuality` instances. | [StreamingModelBuilders.cs](../testkit/Builders/StreamingModelBuilders.cs) |
| `StreamingSearchResultBuilder` | Fluent builder for `StreamingSearchResult` instances. | [StreamingModelBuilders.cs](../testkit/Builders/StreamingModelBuilders.cs) |

## Assertions

One-shot static helpers for validating plugin contracts in tests.

| Type | Purpose | Source |
|------|---------|--------|
| `FileNameAssertions` | Validates file naming contracts across streaming plugins. | [FileNameAssertions.cs](../testkit/Assertions/FileNameAssertions.cs) |
| `PluginAssertions` | General plugin assertion helpers. | [PluginAssertions.cs](../testkit/Assertions/PluginAssertions.cs) |
| `LogAssertions` | Assertion helpers for verifying log output. | [LogAssertions.cs](../testkit/Assertions/LogAssertions.cs) |

## Helpers, Fakes, and Utilities

| Type | Purpose | Source |
|------|---------|--------|
| `FakeTimeProvider` | Deterministic `TimeProvider` for time-dependent tests. | [FakeTimeProvider.cs](../testkit/Testing/FakeTimeProvider.cs) |
| `NullUniversalAdaptiveRateLimiter` | No-op `IUniversalAdaptiveRateLimiter` for tests. | [NullUniversalAdaptiveRateLimiter.cs](../testkit/Fakes/NullUniversalAdaptiveRateLimiter.cs) |
| `NLogTestLogger` | NLog-based logger for test output. | [NLogTestLogger.cs](../testkit/Helpers/NLogTestLogger.cs) |
| `EmbeddedJson` | Reads embedded JSON resource files as test data. | [EmbeddedJson.cs](../testkit/Data/EmbeddedJson.cs) |
| `ModelValidationHelpers` | Static helpers for validating `StreamingModel` objects. | [ModelValidationHelpers.cs](../testkit/Validation/ModelValidationHelpers.cs) |
| `MockFactories` | Factory methods for mock streaming service data. | [MockFactories.cs](../src/Testing/MockFactories.cs) |
| `TestDataSets` | Common test data sets for streaming scenarios. | [MockFactories.cs](../src/Testing/MockFactories.cs) |
| `PackagingTestPaths` | Resolves paths used by packaging integration tests. | [PackagingTestPaths.cs](../testkit/Packaging/PackagingTestPaths.cs) |
| `PackagingFactAttribute` | Base attribute for packaging test facts. | [PackagingFactAttribute.cs](../testkit/Packaging/PackagingFactAttribute.cs) |

## Related Docs

- [TESTING_WITH_TESTKIT.md](../docs/TESTING_WITH_TESTKIT.md) — full usage guide for fixtures, HTTP handlers, and manifest helpers.
- [TEST_PLUGIN.md](../docs/how-to/TEST_PLUGIN.md) — step-by-step: how to test a plugin end-to-end.
- [PERSISTENT_E2E_TESTING.md](../docs/PERSISTENT_E2E_TESTING.md) — long-running end-to-end test environment.
- [MULTI_PLUGIN_SMOKE_TEST.md](../docs/MULTI_PLUGIN_SMOKE_TEST.md) — multi-plugin smoke test infrastructure.
