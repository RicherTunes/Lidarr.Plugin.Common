# SDK and Extension Points

This page maps the types a plugin author **implements or extends** to build on Common.
It links to source and canonical docs — it does not restate them.

Start at [Plugin Registration](#plugin-registration) and branch out by capability.

For the host-owned ABI contracts and cross-ALC boundary rules, see
[Abstractions](../docs/ABSTRACTIONS.md).

## Plugin Registration

These types form the entry point every plugin must satisfy.

| Type | Purpose | Source / Doc |
|------|---------|-------------|
| `IPlugin` | Host-facing contract — manifest, initialization, factory methods for indexer/download client. | [ABSTRACTIONS.md](../docs/ABSTRACTIONS.md) &middot; [IPlugin.cs](../src/Abstractions/Contracts/IPlugin.cs) |
| `StreamingPlugin<TModule, TSettings>` | Base `IPlugin` implementation that wires DI, manifest loading, and strongly typed settings. Derive from this to focus on domain logic. | [PLUGIN_BRIDGE.md](../docs/PLUGIN_BRIDGE.md) &middot; [StreamingPlugin.cs](../src/Hosting/StreamingPlugin.cs) |
| `StreamingPluginModule` | Abstract base for declaring service registrations, capabilities, and metadata. Consumed by `StreamingPlugin<TModule, TSettings>`. | [StreamingPluginModule.cs](../src/Services/Registration/StreamingPluginModule.cs) |
| `PluginManifest` | Immutable metadata loaded from `plugin.json` (id, version, apiVersion, capabilities, compatibility checks). | [PLUGIN_MANIFEST.md](../docs/PLUGIN_MANIFEST.md) &middot; [PluginManifest.cs](../src/Abstractions/Manifest/PluginManifest.cs) |
| `PluginCapability` | Flags enum a module advertises (indexer, download client, caching, OAuth, hi-res audio, etc.). | [PluginCapability.cs](../src/Abstractions/Capabilities/PluginCapability.cs) |

## Indexer — Search and Discovery

Implement these to expose search/indexing to the host.

| Type | Purpose | Source / Doc |
|------|---------|-------------|
| `IIndexer` | Core search contract — album/track search, streaming enumerables, album lookup by id. | [ABSTRACTIONS.md](../docs/ABSTRACTIONS.md) &middot; [IIndexer.cs](../src/Abstractions/Contracts/IIndexer.cs) |
| `IIndexerWithMetadata` | Extends `IIndexer` with protocol, page size, service name, and RSS support flag for host UI routing. | [IIndexer.cs:50](../src/Abstractions/Contracts/IIndexer.cs) |
| `BaseStreamingIndexer<TSettings>` | Shared base: authentication, request building, pagination helpers, deduplication, performance monitoring. Override abstract members for service-specific behaviour. | [BaseStreamingIndexer.cs](../src/Base/BaseStreamingIndexer.cs) |
| `IIndexerRequestBuilder` | Optional seam to decompose request construction. | [IIndexerRequestBuilder.cs](../src/Abstractions/Contracts/IIndexerRequestBuilder.cs) |
| `IIndexerResponseParser<T>` | Optional seam to decompose response parsing. | [IIndexerResponseParser.cs](../src/Abstractions/Contracts/IIndexerResponseParser.cs) |
| `IIndexerStatusReporter` | Reports indexer health/state back to the host. | [IIndexerStatusReporter.cs](../src/Abstractions/Contracts/IIndexerStatusReporter.cs) |
| `IRateLimitReporter` | Reports rate-limit status back to the host. | [IRateLimitReporter.cs](../src/Abstractions/Contracts/IRateLimitReporter.cs) |
| `IRssFeedProvider` | Optional RSS feed capability for indexers that support it. | [IRssFeedProvider.cs](../src/Abstractions/Contracts/IRssFeedProvider.cs) |
| `StreamingIndexerHelpers` | Static helpers for indexer operations (paging, normalization). | [StreamingIndexerHelpers.cs](../src/Base/StreamingIndexerHelpers.cs) |

## Download Client — Fetching Content

Implement these to handle download orchestration.

| Type | Purpose | Source / Doc |
|------|---------|-------------|
| `IDownloadClient` | Host-facing contract — enqueue, remove, and query downloads. | [ABSTRACTIONS.md](../docs/ABSTRACTIONS.md) &middot; [IDownloadClient.cs](../src/Abstractions/Contracts/IDownloadClient.cs) |
| `BaseStreamingDownloadClient<TSettings>` | Shared base: concurrency control, retry logic with resume, metadata tagging, progress tracking. | [BaseStreamingDownloadClient.cs](../src/Base/BaseStreamingDownloadClient.cs) |
| `IStreamingDownloadOrchestrator` | Plugin-internal seam for coordinating album/track downloads and quality selection. | [IStreamingTokenProvider.cs:60](../src/Interfaces/IStreamingTokenProvider.cs) |
| `IDownloadStatusReporter` | Reports download progress/state back to the host. | [IDownloadStatusReporter.cs](../src/Abstractions/Contracts/IDownloadStatusReporter.cs) |

## Authentication and Tokens

Plug in auth flows and token lifecycle.

| Type | Purpose | Source |
|------|---------|--------|
| `IAuthFailureHandler` | Propagates auth failures to the host for UI notification; reports status and recovery. | [IAuthFailureHandler.cs](../src/Abstractions/Contracts/IAuthFailureHandler.cs) |
| `IStreamingAuthenticationService<TSession, TCredentials>` | Generic auth service with session and credential types. | [IStreamingAuthenticationService.cs](../src/Interfaces/IStreamingAuthenticationService.cs) |
| `IStreamingTokenProvider` | Access/refresh token lifecycle, validation, and cache clearing. | [IStreamingTokenProvider.cs](../src/Interfaces/IStreamingTokenProvider.cs) |
| `ITokenStore<TSession>` | Persistent storage for token sessions. | [ITokenStore.cs](../src/Interfaces/ITokenStore.cs) |
| `ITokenProtector` | Encrypts/decrypts token data at rest. | [ITokenProtector.cs](../src/Interfaces/ITokenProtector.cs) |
| `IStringProtector` | General-purpose string encryption (DPAPI-style). | [IStringProtector.cs](../src/Interfaces/IStringProtector.cs) |

## HTTP and Resilience

Seams for signing, caching, and rate-limiting HTTP calls.

| Type | Purpose | Source |
|------|---------|--------|
| `IHttpRequestSigner` | Request-level signing seam for APIs whose auth is computed over the fully-built request. | [IHttpRequestSigner.cs](../src/Services/Http/IHttpRequestSigner.cs) |
| `ICachePolicyProvider` | Supplies cache policies for upstream API responses. | [ICachePolicyProvider.cs](../src/Interfaces/ICachePolicyProvider.cs) |
| `IStreamingResponseCache` | Caches streaming API responses. | [IStreamingResponseCache.cs](../src/Interfaces/IStreamingResponseCache.cs) |
| `IConditionalRequestState` | Manages ETag / Last-Modified conditional-request state. | [IConditionalRequestState.cs](../src/Interfaces/IConditionalRequestState.cs) |
| `IRateLimitObserver` | Observes and reacts to upstream rate-limit signals. | [IRateLimitObserver.cs](../src/Interfaces/IRateLimitObserver.cs) |

## Audio Pipeline

Hooks for audio stream handling, post-processing, and metadata.

| Type | Purpose | Source |
|------|---------|--------|
| `IAudioStreamProvider` | Provides audio streams for download. | [IAudioStreamProvider.cs](../src/Interfaces/IAudioStreamProvider.cs) |
| `IAudioPostProcessor` | Post-download audio processing hook. | [IAudioPostProcessor.cs](../src/Interfaces/IAudioPostProcessor.cs) |
| `IAudioMetadataApplier` | Applies metadata tags to downloaded files (consumed by `BaseStreamingDownloadClient`). | [IAudioMetadataApplier.cs](../src/Interfaces/IAudioMetadataApplier.cs) |

## LLM Integration

| Type | Purpose | Source |
|------|---------|--------|
| `ILlmProvider` | Core abstraction for LLM providers (streaming, tool calls, usage tracking). | [ILlmProvider.cs](../src/Abstractions/Llm/ILlmProvider.cs) |

## Host Bridge

Types that span the plugin/host boundary. See [PLUGIN_BRIDGE.md](../docs/PLUGIN_BRIDGE.md) for the full bridge model.

| Type | Purpose | Source |
|------|---------|--------|
| `HostBridgeRuntimeCache<TRuntime, TSettings>` | Abstract base for runtime caching across the plugin/host boundary. | [HostBridgeRuntimeCache.cs](../src/HostBridge/HostBridgeRuntimeCache.cs) |
| `HostBridgeDownloadOrchestrator` | Coordinates downloads across the bridge. | [HostBridgeDownloadOrchestrator.cs](../src/HostBridge/HostBridgeDownloadOrchestrator.cs) |
| `HostBridgeDownloadTrackerStore<TItem>` | Tracks download items in bridge storage. | [HostBridgeDownloadTracker.cs](../src/HostBridge/HostBridgeDownloadTracker.cs) |
| `AlbumReleaseInfoBuilder` | Builds album release info for the host. | [AlbumReleaseInfoBuilder.cs](../src/HostBridge/AlbumReleaseInfoBuilder.cs) |
| `AlbumDownloadUri` | Static helper for building album download URIs. | [AlbumDownloadUri.cs](../src/HostBridge/AlbumDownloadUri.cs) |
| `PrefixedReleaseGuidParser` | Static helper for parsing prefixed release GUIDs. | [PrefixedReleaseGuidParser.cs](../src/HostBridge/PrefixedReleaseGuidParser.cs) |
| `PathTraversalGuard` | Static guard against path-traversal attacks in bridge paths. | [PathTraversalGuard.cs](../src/HostBridge/PathTraversalGuard.cs) |
| `PlaceholderSearchUri` | Static helper for placeholder search URIs. | [PlaceholderSearchUri.cs](../src/HostBridge/PlaceholderSearchUri.cs) |

## Settings

| Type | Purpose | Source / Doc |
|------|---------|-------------|
| `BaseStreamingSettings` | Abstract base with common properties (BaseUrl, auth fields, rate limit, caching, locale). Derive for service-specific settings. | [BaseStreamingSettings.cs](../src/Base/BaseStreamingSettings.cs) |
| `ISettingsProvider` | Host-facing settings operations (describe, validate, apply). | [SETTINGS_PROVIDER.md](../docs/SETTINGS_PROVIDER.md) &middot; [ISettingsProvider.cs](../src/Abstractions/Contracts/ISettingsProvider.cs) |
| `IPluginContext` | Runtime context provided to the plugin during initialization. | [IPluginContext.cs](../src/Abstractions/Contracts/IPluginContext.cs) |

## Related Docs

- [Plugin Bridge](../docs/PLUGIN_BRIDGE.md) — `StreamingPlugin<TModule,TSettings>` quickstart and extension model.
- [Plugin Manifest](../docs/PLUGIN_MANIFEST.md) — manifest schema and compatibility checks.
- [Plugin Isolation](../docs/PLUGIN_ISOLATION.md) — how plugins load inside dedicated AssemblyLoadContexts.
- [Abstractions](../docs/ABSTRACTIONS.md) — host-owned ABI (`IPlugin`, `IIndexer`, `IDownloadClient`).
- [Streaming Support](../docs/STREAMING_SUPPORT.md) — streaming feature coverage.
- [Testing with the TestKit](../docs/TESTING_WITH_TESTKIT.md) — fixtures, HTTP handlers, manifest helpers.
- [FAQ for Plugin Authors](../docs/FAQ_FOR_PLUGIN_AUTHORS.md) — common pitfalls and patterns.
- [Glossary](../docs/GLOSSARY.md) — shared terminology.
