# Shared Helpers Catalog

Quick-reference catalog of the reusable helpers in **Lidarr.Plugin.Common** that a plugin author consumes.
Each entry is a one-line purpose and a link to its source; full details live in the linked docs and code.

For architecture context, see [Architecture Overview](Architecture-Overview.md) and the
[doc hub](../docs/README.md).

---

## Security

| Helper | Purpose | Source |
|--------|---------|--------|
| `PathTraversalGuard` | Blocks directory-traversal attacks in download paths | [`HostBridge/PathTraversalGuard.cs`](../src/HostBridge/PathTraversalGuard.cs) |
| `Sanitize` | Static helpers for sanitising user-supplied strings | [`Security/Sanitize.cs`](../src/Security/Sanitize.cs) |
| `SecureMemory` | Pin and clear cryptographic key material from the GC heap | [`Security/SecureMemory.cs`](../src/Security/SecureMemory.cs) |
| `SecureCredentialManager` | Holds and disposes credentials in pinned memory | [`Security/SecureCredentialManager.cs`](../src/Security/SecureCredentialManager.cs) |
| `TokenProtectorFactory` | Creates the platform-appropriate `ITokenProtector` (DPAPI / Keychain / SecretService) | [`Security/TokenProtection/TokenProtectorFactory.cs`](../src/Security/TokenProtection/TokenProtectorFactory.cs) |
| `LlmPromptSanitizer` | Strips sensitive data from LLM prompts before logging | [`Security/Llm/LlmPromptSanitizer.cs`](../src/Security/Llm/LlmPromptSanitizer.cs) |
| `LlmJsonSerializer` | Deterministic JSON serialisation with PII redaction for LLM payloads | [`Security/Llm/LlmJsonSerializer.cs`](../src/Security/Llm/LlmJsonSerializer.cs) |

See also: [Security Hardening Overview](../docs/SECURITY_HARDENING_OVERVIEW.md),
[COM-005/COM-011 Remediation](../docs/SECURITY/COM-005-COM-011-REMEDIATION.md),
[Phase 3.1 — SecureMemory](../docs/SECURITY/COMMON-HELPERS-PHASE-3-1.md).

---

## Resilience

| Helper | Purpose | Source |
|--------|---------|--------|
| `ICircuitBreaker` | Circuit-breaker contract with state-change events | [`Services/Resilience/ICircuitBreaker.cs`](../src/Services/Resilience/ICircuitBreaker.cs) |
| `CircuitBreaker` | Count-based circuit-breaker implementation | [`Services/Resilience/CircuitBreaker.cs`](../src/Services/Resilience/CircuitBreaker.cs) |
| `AdvancedCircuitBreaker` | Adaptive circuit-breaker with per-endpoint health tracking | [`Services/Resilience/AdvancedCircuitBreaker.cs`](../src/Services/Resilience/AdvancedCircuitBreaker.cs) |
| `IRetryPolicy` / `ExponentialBackoffRetryPolicy` | Retry policy with configurable back-off | [`Services/Resilience/RetryPolicy.cs`](../src/Services/Resilience/RetryPolicy.cs) |
| `DefensiveServiceWrapper<T>` | Wraps a service so every call is guarded by retry + circuit-breaker | [`Services/Resilience/DefensiveServiceWrapper.cs`](../src/Services/Resilience/DefensiveServiceWrapper.cs) |
| `IResilienceSettingsProvider` | Named resilience profiles ("auth", "search", "download", etc.) | [`Services/Resilience/IResilienceSettingsProvider.cs`](../src/Services/Resilience/IResilienceSettingsProvider.cs) |
| `StaticResiliencePolicyProvider` | Hard-coded profile map for simple plugins | [`Services/Resilience/StaticResiliencePolicyProvider.cs`](../src/Services/Resilience/StaticResiliencePolicyProvider.cs) |
| `FileResiliencePolicyProvider` | Hot-reloadable profiles from a JSON file | [`Services/Resilience/FileResiliencePolicyProvider.cs`](../src/Services/Resilience/FileResiliencePolicyProvider.cs) |
| `BackendHealthCache` | Marks a backend host as "down" after connection-class failures | [`Resilience/BackendHealthCache.cs`](../src/Resilience/BackendHealthCache.cs) |
| `TokenBucketRateLimiter` | Token-bucket rate limiter with presets | [`Services/Resilience/TokenBucketRateLimiter.cs`](../src/Services/Resilience/TokenBucketRateLimiter.cs) |
| `RequestDeduplicator` | Coalesces identical concurrent requests into a single flight | [`Services/Deduplication/RequestDeduplicator.cs`](../src/Services/Deduplication/RequestDeduplicator.cs) |
| `NetworkResilienceService` | Batch operations with adaptive retries and network-health monitoring | [`Services/Network/NetworkResilienceService.cs`](../src/Services/Network/NetworkResilienceService.cs) |

---

## HTTP Pipeline

| Helper | Purpose | Source |
|--------|---------|--------|
| `PluginHttpOptions` | Named HTTP client options constants for DI registration | [`Services/Http/PluginHttpOptions.cs`](../src/Services/Http/PluginHttpOptions.cs) |
| `DefaultProfiles` | Pre-built resilience profiles for standard HTTP operations | [`Services/Http/DefaultProfiles.cs`](../src/Services/Http/DefaultProfiles.cs) |
| `CachingHttpExecutor` | Executes HTTP requests with ETag/Last-Modified revalidation | [`Services/Http/CachingHttpExecutor.cs`](../src/Services/Http/CachingHttpExecutor.cs) |
| `BackendHealthDelegatingHandler` | `DelegatingHandler` that short-circuits when the backend is marked down | [`Services/Http/BackendHealthDelegatingHandler.cs`](../src/Services/Http/BackendHealthDelegatingHandler.cs) |
| `AdaptiveRateLimitingHandler` | `DelegatingHandler` that throttles requests based on 429 headers | [`Services/Http/AdaptiveRateLimitingHandler.cs`](../src/Services/Http/AdaptiveRateLimitingHandler.cs) |
| `DiagnosticTapHandler` | `DelegatingHandler` that logs request/response telemetry | [`Services/Http/DiagnosticTapHandler.cs`](../src/Services/Http/DiagnosticTapHandler.cs) |
| `ContentDecodingSnifferHandler` | Detects and decompresses mislabeled content encodings | [`Services/Http/ContentDecodingSnifferHandler.cs`](../src/Services/Http/ContentDecodingSnifferHandler.cs) |
| `HttpResponseHelpers` | Static helpers for reading and validating HTTP responses | [`Services/Http/HttpResponseHelpers.cs`](../src/Services/Http/HttpResponseHelpers.cs) |
| `OAuthDelegatingHandler` | `DelegatingHandler` that injects OAuth bearer tokens | [`Services/Http/OAuthDelegatingHandler.cs`](../src/Services/Http/OAuthDelegatingHandler.cs) |
| `IRequestSigner` | Signs a parameter dictionary (e.g. Qobuz MD5 concat) | [`Utilities/RequestSigning.cs`](../src/Utilities/RequestSigning.cs) |
| `IHttpRequestSigner` | Signs a fully-built `HttpRequestMessage` (e.g. ADP RSA-SHA256) | [`Services/Http/IHttpRequestSigner.cs`](../src/Services/Http/IHttpRequestSigner.cs) |

See also: [HTTP Defaults](../docs/HTTPDefaults.md), [HTTP Flow](../docs/Flow.md).

---

## Authentication & Tokens

| Helper | Purpose | Source |
|--------|---------|--------|
| `BaseStreamingAuthenticationService<T,S>` | Template-method base for streaming-service auth flows | [`Services/Authentication/BaseStreamingAuthenticationService.cs`](../src/Services/Authentication/BaseStreamingAuthenticationService.cs) |
| `OAuthStreamingAuthenticationService<T,S>` | OAuth 2.0 + PKCE authentication base class | [`Services/Authentication/OAuthStreamingAuthenticationService.cs`](../src/Services/Authentication/OAuthStreamingAuthenticationService.cs) |
| `StreamingTokenManager<T,S>` | Manages token lifecycle (acquire, refresh, revoke) | [`Services/Authentication/StreamingTokenManager.cs`](../src/Services/Authentication/StreamingTokenManager.cs) |
| `FileTokenStore<T>` | Persisted, encrypted token store backed by a JSON file | [`Services/Authentication/FileTokenStore.cs`](../src/Services/Authentication/FileTokenStore.cs) |
| `AuthFailureGate` | Latches auth failures so downstream code sees a consistent "auth broken" state | [`Services/Bridge/AuthFailureGate.cs`](../src/Services/Bridge/AuthFailureGate.cs) |
| `AuthFailureDelegatingHandler` | `DelegatingHandler` that triggers the gate on 401/403 | [`Services/Bridge/AuthFailureDelegatingHandler.cs`](../src/Services/Bridge/AuthFailureDelegatingHandler.cs) |
| `SlidingWindowAuthFailureHandler` | Rate-limits auth-retry attempts with a sliding window | [`Services/Bridge/SlidingWindowAuthFailureHandler.cs`](../src/Services/Bridge/SlidingWindowAuthFailureHandler.cs) |

Key interfaces: `ITokenStore<T>`, `ITokenProtector`, `IStringProtector`, `IStreamingAuthenticationService<T,S>`,
`IAuthFailureHandler` — see [`Interfaces/`](../src/Interfaces/) and
[`Abstractions/Contracts/`](../src/Abstractions/Contracts/).

---

## Caching

| Helper | Purpose | Source |
|--------|---------|--------|
| `StreamingResponseCache` | Abstract base for API response caching with ETag support | [`Services/Caching/StreamingResponseCache.cs`](../src/Services/Caching/StreamingResponseCache.cs) |
| `FileStreamingResponseCache` | Disk-backed response cache implementation | [`Services/Caching/FileStreamingResponseCache.cs`](../src/Services/Caching/FileStreamingResponseCache.cs) |
| `SmartCache<K,V>` | Size-bounded cache with eviction statistics and priority levels | [`Services/Caching/SmartCache.cs`](../src/Services/Caching/SmartCache.cs) |
| `CachePolicy` | TTL, staleness, and hit-mode configuration for cache entries | [`Services/Caching/CachePolicy.cs`](../src/Services/Caching/CachePolicy.cs) |
| `ArrCachingHeaders` | Lidarr-standard cache-control header constants | [`Services/Caching/ArrCachingHeaders.cs`](../src/Services/Caching/ArrCachingHeaders.cs) |
| `IStreamingResponseCache` | Generic response-cache contract | [`Interfaces/IStreamingResponseCache.cs`](../src/Interfaces/IStreamingResponseCache.cs) |
| `ICacheStorage<T>` / `ICacheSerializer<T>` / `ICacheEvictionStrategy<T>` | Pluggable cache storage seams | [`Services/Caching/`](../src/Services/Caching/) |

---

## Download Pipeline

| Helper | Purpose | Source |
|--------|---------|--------|
| `SimpleDownloadOrchestrator` | Orchestrates multi-track downloads with retry and telemetry | [`Services/Download/SimpleDownloadOrchestrator.cs`](../src/Services/Download/SimpleDownloadOrchestrator.cs) |
| `AlbumCompletionPolicy` | Decides whether an album download is complete | [`Services/Download/AlbumCompletionPolicy.cs`](../src/Services/Download/AlbumCompletionPolicy.cs) |
| `ChunkedHttpAssembler` | Downloads and reassembles chunked files | [`Services/Download/ChunkedHttpAssembler.cs`](../src/Services/Download/ChunkedHttpAssembler.cs) |
| `HttpFileDownloadService` | Default `IHttpFileDownloadService` implementation | [`Services/Download/HttpFileDownloadService.cs`](../src/Services/Download/HttpFileDownloadService.cs) |
| `DownloadPathValidator` | Validates download destination paths (traversal, length, special names) | [`Services/Validation/DownloadPathValidator.cs`](../src/Services/Validation/DownloadPathValidator.cs) |
| `DownloadTelemetry` | Canonical per-track telemetry record shared by all plugins | [`Services/Download/DownloadTelemetry.cs`](../src/Services/Download/DownloadTelemetry.cs) |
| `DrmTrack` | Represents a DRM-protected track for external download delegation | [`Services/Drm/DrmTrack.cs`](../src/Services/Drm/DrmTrack.cs) |
| `IExternalDownloadHandler` | Seam for delegating DRM downloads to an external handler | [`Services/Drm/IExternalDownloadHandler.cs`](../src/Services/Drm/IExternalDownloadHandler.cs) |

---

## Streaming (SSE / LLM)

| Helper | Purpose | Source |
|--------|---------|--------|
| `SseFramingReader` | Low-level SSE frame parser (field-per-line) | [`Streaming/SseFramingReader.cs`](../src/Streaming/SseFramingReader.cs) |
| `IStreamDecoder` | Adapts a provider-specific SSE stream into `LlmStreamChunk` records | [`Streaming/IStreamDecoder.cs`](../src/Streaming/IStreamDecoder.cs) |
| `OpenAiStreamDecoder` | Decoder for OpenAI-compatible SSE streams | [`Streaming/Decoders/OpenAiStreamDecoder.cs`](../src/Streaming/Decoders/OpenAiStreamDecoder.cs) |
| `AnthropicStreamDecoder` | Decoder for Anthropic SSE streams | [`Streaming/Decoders/AnthropicStreamDecoder.cs`](../src/Streaming/Decoders/AnthropicStreamDecoder.cs) |
| `GeminiStreamDecoder` | Decoder for Google Gemini SSE streams | [`Streaming/Decoders/GeminiStreamDecoder.cs`](../src/Streaming/Decoders/GeminiStreamDecoder.cs) |
| `ZaiStreamDecoder` | Decoder for Z.AI/GLM SSE streams | [`Streaming/Decoders/ZaiStreamDecoder.cs`](../src/Streaming/Decoders/ZaiStreamDecoder.cs) |
| `StreamingCancellation` | Cooperative cancellation with reason tracking | [`Streaming/StreamingCancellation.cs`](../src/Streaming/StreamingCancellation.cs) |
| `StreamingTimeoutPolicy` | Configurable timeout record for streaming operations | [`Streaming/StreamingTimeoutPolicy.cs`](../src/Streaming/StreamingTimeoutPolicy.cs) |
| `RateLimitedEventLogger` | Throttles repeated rate-limit log messages | [`Streaming/RateLimitedEventLogger.cs`](../src/Streaming/RateLimitedEventLogger.cs) |

LLM abstractions (`ILlmProvider`, `LlmRequest`, `LlmResponse`, `LlmStreamChunk`, `LlmUsage`,
`LlmTool`, `LlmToolCall`, `LlmToolResult`, `LlmThinkingHint`) live in
[`Abstractions/Llm/`](../src/Abstractions/Llm/).
The Claude Code provider implementation is in [`Providers/ClaudeCode/`](../src/Providers/ClaudeCode/).

---

## CLI Framework

Streaming plugins that ship a standalone CLI inherit from `BaseStreamingCLI<TSettings>`, which provides 80%+ of the command-line logic out of the box. Six command classes are included:

| Helper | Purpose | Source |
|--------|---------|--------|
| `BaseStreamingCLI<TSettings>` | Base CLI framework (DI, config, logging, root command wiring) | [`CLI/BaseStreamingCLI.cs`](../src/CLI/BaseStreamingCLI.cs) |
| `AuthCommand<T>` | `auth login/logout/status` subcommands | [`CLI/Commands/BaseCommand.cs`](../src/CLI/Commands/BaseCommand.cs) |
| `SearchCommand<T>` | `search --limit` subcommand | [`CLI/Commands/BaseCommand.cs`](../src/CLI/Commands/BaseCommand.cs) |
| `DownloadCommand<T>` | `download --output` subcommand | [`CLI/Commands/BaseCommand.cs`](../src/CLI/Commands/BaseCommand.cs) |
| `ConfigCommand<T>` | `config show/set/get/reset` subcommands | [`CLI/Commands/BaseCommand.cs`](../src/CLI/Commands/BaseCommand.cs) |
| `QueueCommand<T>` | `queue status/list/clear/pause/resume/dashboard` subcommands | [`CLI/Commands/BaseCommand.cs`](../src/CLI/Commands/BaseCommand.cs) |
| `HistoryCommand<T>` | `history show/clear/stats` subcommands | [`CLI/Commands/BaseCommand.cs`](../src/CLI/Commands/BaseCommand.cs) |

Override `ConfigureServices` and `ConfigureCommands` on `BaseStreamingCLI<TSettings>` to add service-specific commands or DI registrations.

---

## Bridge & Host Integration

| Helper | Purpose | Source |
|--------|---------|--------|
| `PathTraversalGuard` | Validates paths passed to the host are within allowed directories | [`HostBridge/PathTraversalGuard.cs`](../src/HostBridge/PathTraversalGuard.cs) |
| `HostBridgeDownloadTrackerStore<T>` | Per-download state tracker that survives host round-trips | [`HostBridge/HostBridgeDownloadTracker.cs`](../src/HostBridge/HostBridgeDownloadTracker.cs) |
| `HostBridgeDownloadOrchestrator` | Coordinates bridge download lifecycle (track → item → completion) | [`HostBridge/HostBridgeDownloadOrchestrator.cs`](../src/HostBridge/HostBridgeDownloadOrchestrator.cs) |
| `HostBridgeRuntimeCache<T,S>` | Caches auth tokens and configuration across host re-loads | [`HostBridge/HostBridgeRuntimeCache.cs`](../src/HostBridge/HostBridgeRuntimeCache.cs) |
| `AlbumReleaseInfoBuilder` | Builds the release-info payload Lidarr expects | [`HostBridge/AlbumReleaseInfoBuilder.cs`](../src/HostBridge/AlbumReleaseInfoBuilder.cs) |
| `AlbumDownloadUri` | Constructs album-level download URIs for the host | [`HostBridge/AlbumDownloadUri.cs`](../src/HostBridge/AlbumDownloadUri.cs) |
| `PlaceholderSearchUri` | Returns a placeholder search URI for the initial host query | [`HostBridge/PlaceholderSearchUri.cs`](../src/HostBridge/PlaceholderSearchUri.cs) |
| `StreamingPlugin<T,S>` | Abstract base class that wires a plugin into the host lifecycle | [`Hosting/StreamingPlugin.cs`](../src/Hosting/StreamingPlugin.cs) |
| `StreamingPluginModule` | DI module base class with auto-registration attribute scanning | [`Services/Registration/StreamingPluginModule.cs`](../src/Services/Registration/StreamingPluginModule.cs) |
| `PluginLifecycle` | Startup/shutdown hooks for the plugin lifecycle | [`Hosting/PluginLifecycle.cs`](../src/Hosting/PluginLifecycle.cs) |
| `PluginConfigRoots` | Resolves configuration root directories (XDG / Windows) | [`Hosting/PluginConfigRoots.cs`](../src/Hosting/PluginConfigRoots.cs) |

See also: [Plugin Bridge](../docs/PLUGIN_BRIDGE.md), [Plugin Isolation](../docs/PLUGIN_ISOLATION.md),
[Plugin Manifest](../docs/PLUGIN_MANIFEST.md).

---

## Observability & Diagnostics

| Helper | Purpose | Source |
|--------|---------|--------|
| `WarnOnce` | Emits a log warning only once per key; suppresses duplicates | [`Diagnostics/WarnOnce.cs`](../src/Diagnostics/WarnOnce.cs) |
| `HealthCheckHelper` | Runs provider health checks and formats `DiagnosticHealthResult` | [`Diagnostics/HealthCheckHelper.cs`](../src/Diagnostics/HealthCheckHelper.cs) |
| `HttpExceptionClassifier` | Classifies HTTP exceptions into failure categories | [`Services/Diagnostics/HttpExceptionClassifier.cs`](../src/Services/Diagnostics/HttpExceptionClassifier.cs) |
| `IMetricsRecorder` | Dimensional metrics interface with `NullMetricsRecorder` and `ObservableMetricsRecorder` | [`Observability/IMetricsRecorder.cs`](../src/Observability/IMetricsRecorder.cs) |
| `Metrics` | Lightweight static counters for legacy callers | [`Observability/Metrics.cs`](../src/Observability/Metrics.cs) |
| `PluginLogContext` | Scoped log context that enriches log entries with plugin metadata | [`Observability/PluginLogContext.cs`](../src/Observability/PluginLogContext.cs) |
| `LogRedactor` | Source-generated redactor for sanitising log output | [`Observability/LogRedactor.cs`](../src/Observability/LogRedactor.cs) |
| `Scrub` | Removes sensitive values (tokens, keys) from strings | [`Observability/Scrub.cs`](../src/Observability/Scrub.cs) |
| `LoggerExtensions` / `EventIds` | Structured logging helpers and well-known event IDs | [`Observability/LoggerExtensions.cs`](../src/Observability/LoggerExtensions.cs) |
| `LlmEventIds` / `LlmLoggerExtensions` | Event IDs and log helpers specific to LLM operations | [`Observability/LlmEventIds.cs`](../src/Observability/LlmEventIds.cs) |
| `DownloadDiagnostics` | Helpers for emitting download-specific diagnostic events | [`Utilities/DownloadDiagnostics.cs`](../src/Utilities/DownloadDiagnostics.cs) |

See also: [Telemetry (OpenTelemetry)](../docs/Telemetry.md),
[Telemetry DI Contract](../docs/TELEMETRY_DI_CONTRACT.md),
[Diagnostics Bundle Contract](../docs/DIAGNOSTICS_BUNDLE_CONTRACT.md).

---

## Validation & Guards

| Helper | Purpose | Source |
|--------|---------|--------|
| `Guard` | Precondition checks (not-null, range, enum) | [`Utilities/Guard.cs`](../src/Utilities/Guard.cs) |
| `PathValidation` | Validates paths are safe and within expected roots | [`Utilities/PathValidation.cs`](../src/Utilities/PathValidation.cs) |
| `ValidationUtilities` | Common validation helpers for plugin inputs | [`Utilities/ValidationUtilities.cs`](../src/Utilities/ValidationUtilities.cs) |
| `DataValidationService` | Validates streaming data (duplicates, track sequences, paths) | [`Services/Validation/DataValidationService.cs`](../src/Services/Validation/DataValidationService.cs) |
| `DownloadPayloadValidator` | Validates downloaded payload integrity (size, magic bytes) | [`Utilities/DownloadPayloadValidator.cs`](../src/Utilities/DownloadPayloadValidator.cs) |
| `PackageClosureValidator` | Validates that a download package contains all expected files | [`Utilities/PackageClosureValidator.cs`](../src/Utilities/PackageClosureValidator.cs) |
| `AudioMagicBytesValidator` | Verifies downloaded files match expected audio format signatures | [`Utilities/AudioMagicBytesValidator.cs`](../src/Utilities/AudioMagicBytesValidator.cs) |

---

## Collections

| Helper | Purpose | Source |
|--------|---------|--------|
| `BoundedConcurrentDictionary<K,V>` | Thread-safe dictionary that evicts excess entries when a capacity cap is hit | [`Collections/BoundedConcurrentDictionary.cs`](../src/Collections/BoundedConcurrentDictionary.cs) |
| `CircularBuffer<T>` | Fixed-size ring buffer for sliding-window metrics | [`Services/Resilience/CircularBuffer.cs`](../src/Services/Resilience/CircularBuffer.cs) |

---

## Utilities

| Helper | Purpose | Source |
|--------|---------|--------|
| `RetryUtilities` | Single source of truth for retry delay calculations | [`Utilities/RetryUtilities.cs`](../src/Utilities/RetryUtilities.cs) |
| `ResiliencePolicy` | Lightweight resilience policy wrapper | [`Utilities/ResiliencePolicy.cs`](../src/Utilities/ResiliencePolicy.cs) |
| `GenericResilienceExecutor` | Executes delegates with configurable retry and timeout | [`Utilities/GenericResilienceExecutor.cs`](../src/Utilities/GenericResilienceExecutor.cs) |
| `HttpClientExtensions` | Extension methods for common HTTP patterns (download, read-as) | [`Utilities/HttpClientExtensions.cs`](../src/Utilities/HttpClientExtensions.cs) |
| `FileSystemUtilities` | Safe file-system helpers (atomic write, temp paths) | [`Utilities/FileSystemUtilities.cs`](../src/Utilities/FileSystemUtilities.cs) |
| `FileNameSanitizer` | Sanitises file names for cross-platform safety | [`Utilities/FileNameSanitizer.cs`](../src/Utilities/FileNameSanitizer.cs) |
| `HashingUtility` | SHA/MD5 hashing helpers | [`Utilities/HashingUtility.cs`](../src/Utilities/HashingUtility.cs) |
| `QueryCanonicalizer` | Normalises query strings for cache-key consistency | [`Utilities/QueryCanonicalizer.cs`](../src/Utilities/QueryCanonicalizer.cs) |
| `SensitiveKeys` | Known key names that should be redacted from logs | [`Utilities/SensitiveKeys.cs`](../src/Utilities/SensitiveKeys.cs) |
| `HostConcurrencyGate` | Limits concurrent operations per host | [`Utilities/HostConcurrencyGate.cs`](../src/Utilities/HostConcurrencyGate.cs) |
| `DownloadTelemetryContext` | Async-local scope that tracks per-download telemetry counters | [`Utilities/DownloadTelemetryContext.cs`](../src/Utilities/DownloadTelemetryContext.cs) |
| `PreviewDetectionUtility` | Detects preview/early-access content from metadata | [`Utilities/PreviewDetectionUtility.cs`](../src/Utilities/PreviewDetectionUtility.cs) |

---

## Intelligence & Metadata

| Helper | Purpose | Source |
|--------|---------|--------|
| `LyricsEnricher` | Fetches and embeds synchronized lyrics via `ILyricsEnricher` | [`Services/Lyrics/LyricsEnricher.cs`](../src/Services/Lyrics/LyricsEnricher.cs) |
| `LrclibClient` | Client for the LRCLIB lyrics API | [`Services/Lyrics/LrclibClient.cs`](../src/Services/Lyrics/LrclibClient.cs) |
| `UnicodeNormalizer` | Normalises Unicode strings (NFC/NFD, script detection) | [`Services/Globalization/UnicodeNormalizer.cs`](../src/Services/Globalization/UnicodeNormalizer.cs) |
| `MetadataFieldSanitizer` | Cleans metadata fields (whitespace, control chars) | [`Services/Intelligence/MetadataFieldSanitizer.cs`](../src/Services/Intelligence/MetadataFieldSanitizer.cs) |
| `LiveAlbumNormalizer` | Detects and normalises live album metadata | [`Services/Intelligence/LiveAlbumNormalizer.cs`](../src/Services/Intelligence/LiveAlbumNormalizer.cs) |
| `CompilationAlbumDetector` | Detects compilation albums from metadata patterns | [`Services/Intelligence/CompilationAlbumDetector.cs`](../src/Services/Intelligence/CompilationAlbumDetector.cs) |
| `IQueryOptimizer` | Interface for search-query optimisation with feedback loops | [`Services/Intelligence/IQueryOptimizer.cs`](../src/Services/Intelligence/IQueryOptimizer.cs) |
| `QualityMapper` | Maps streaming quality tiers to Lidarr quality profiles | [`Services/Quality/QualityMapper.cs`](../src/Services/Quality/QualityMapper.cs) |

---

## Performance & Rate Limiting

| Helper | Purpose | Source |
|--------|---------|--------|
| `UniversalAdaptiveRateLimiter` | Self-tuning rate limiter that adapts to 429/backoff signals | [`Services/Performance/UniversalAdaptiveRateLimiter.cs`](../src/Services/Performance/UniversalAdaptiveRateLimiter.cs) |
| `NamedServiceRateLimiter` | Per-service rate limiter base class for multi-service plugins | [`Services/Performance/NamedServiceRateLimiter.cs`](../src/Services/Performance/NamedServiceRateLimiter.cs) |
| `AdaptiveConcurrencyManager` | Dynamically adjusts concurrency based on throughput and errors | [`Services/Performance/AdaptiveConcurrencyManager.cs`](../src/Services/Performance/AdaptiveConcurrencyManager.cs) |
| `MemoryHealthMonitor` | Monitors GC memory pressure and advises on optimisation | [`Services/Performance/MemoryHealthMonitor.cs`](../src/Services/Performance/MemoryHealthMonitor.cs) |
| `BatchMemoryManager` | Manages memory budgets for batch download operations | [`Services/Performance/BatchMemoryManager.cs`](../src/Services/Performance/BatchMemoryManager.cs) |
| `PerformanceMonitor` | Tracks operation durations and throughput metrics | [`Services/Performance/PerformanceMonitor.cs`](../src/Services/Performance/PerformanceMonitor.cs) |

---

## Storage

| Helper | Purpose | Source |
|--------|---------|--------|
| `JsonFileStore<K,V>` | Transactional JSON file store with typed keys and entry metadata | [`Services/Storage/JsonFileStore.cs`](../src/Services/Storage/JsonFileStore.cs) |
| `FileConditionalRequestState` | Persists ETag/Last-Modified state for conditional HTTP requests | [`Services/Http/FileConditionalRequestState.cs`](../src/Services/Http/FileConditionalRequestState.cs) |

---

## DI Extensions

| Helper | Purpose | Source |
|--------|---------|--------|
| `ServiceCollectionExtensions` | Registers Common's default services into the DI container | [`Extensions/ServiceCollectionExtensions.cs`](../src/Extensions/ServiceCollectionExtensions.cs) |
| `BridgeServiceCollectionExtensions` | Registers bridge-specific services (auth gates, reporters) | [`Extensions/BridgeServiceCollectionExtensions.cs`](../src/Extensions/BridgeServiceCollectionExtensions.cs) |
| `DownloadTelemetryServiceCollectionExtensions` | Registers download telemetry services | [`Extensions/DownloadTelemetryServiceCollectionExtensions.cs`](../src/Extensions/DownloadTelemetryServiceCollectionExtensions.cs) |

---

## Errors

| Helper | Purpose | Source |
|--------|---------|--------|
| `LlmProviderException` | Base exception for LLM-provider errors | [`Errors/LlmProviderException.cs`](../src/Errors/LlmProviderException.cs) |
| `RateLimitException` / `ProviderException` / `AuthenticationException` / `NetworkException` | Typed LLM error subclasses | [`Errors/`](../src/Errors/) |
| `DownloadIntegrityException` | Thrown when a downloaded file fails integrity checks | [`Errors/DownloadIntegrityException.cs`](../src/Errors/DownloadIntegrityException.cs) |
| `LlmErrorMapper` | Maps provider-specific error responses to `LlmErrorCode` | [`Errors/LlmErrorMapper.cs`](../src/Errors/LlmErrorMapper.cs) |
| `StreamingException` | Base exception for streaming/SSE errors | [`Streaming/StreamingException.cs`](../src/Streaming/StreamingException.cs) |
| `AuthGatedException` | Thrown when an `AuthFailureGate` is latched | [`Services/Bridge/AuthFailureGate.cs`](../src/Services/Bridge/AuthFailureGate.cs) |

---

## Base Classes (Inherit to Build a Plugin)

| Class | Purpose | Source |
|-------|---------|--------|
| `BaseStreamingIndexer<T>` | Base class for streaming-service indexers | [`Base/BaseStreamingIndexer.cs`](../src/Base/BaseStreamingIndexer.cs) |
| `BaseStreamingDownloadClient<T>` | Base class for streaming-service download clients | [`Base/BaseStreamingDownloadClient.cs`](../src/Base/BaseStreamingDownloadClient.cs) |
| `BaseStreamingSettings` | Abstract settings POCO with common properties | [`Base/BaseStreamingSettings.cs`](../src/Base/BaseStreamingSettings.cs) |
| `StreamingIndexerHelpers` / `StreamingConfigHelpers` | Static composition helpers used by base classes | [`Base/StreamingIndexerHelpers.cs`](../src/Base/StreamingIndexerHelpers.cs) |
| `SafeOperationExecutor` | Executes lambdas with standard defensive patterns | [`Services/SafeOperationExecutor.cs`](../src/Services/SafeOperationExecutor.cs) |
| `LidarrIntegrationHelpers` | Composition helpers for Lidarr host integration | [`Services/LidarrIntegrationHelpers.cs`](../src/Services/LidarrIntegrationHelpers.cs) |

---

## Testing Helpers

| Helper | Purpose | Source |
|--------|---------|--------|
| `MockFactories` / `TestDataSets` | Factory methods and sample data sets for unit tests | [`Testing/MockFactories.cs`](../src/Testing/MockFactories.cs) |
| `TestValidationBuilder` | Fluent builder for constructing test validation scenarios | [`Validation/TestValidationBuilder.cs`](../src/Validation/TestValidationBuilder.cs) |
| `TestFailureFormatter` | Formats test failure messages for diagnostics | [`Utilities/TestFailureFormatter.cs`](../src/Utilities/TestFailureFormatter.cs) |

See also: [Testing with the TestKit](../docs/TESTING_WITH_TESTKIT.md).

---

## Further Reading

- [FAQ for Plugin Authors](../docs/FAQ_FOR_PLUGIN_AUTHORS.md)
- [Upgrading Lidarr.Plugin.Common](../docs/UPGRADING.md)
- [Settings Provider Bridge](../docs/SETTINGS_PROVIDER.md)
- [Packaging Plugins](../docs/PACKAGING.md)
- [Architecture Overview](Architecture-Overview.md)
- [SDK and Extension Points](SDK-and-Extension-Points.md)
