# Architecture Overview

This page is the orientation map for plugin authors.
It tells you **what exists and where to find it** — for the details, follow the links.

## AssemblyLoadContext Isolation

Each plugin loads Common inside its own `AssemblyLoadContext`, so multiple plugins never share types or state.
The host owns the ABI surface via the **Lidarr.Plugin.Abstractions** assembly — everything a plugin sees crosses that boundary.

- How isolation works and why: [PLUGIN_ISOLATION.md](../docs/PLUGIN_ISOLATION.md)
- Multi-plugin ALC validation and edge cases: [MULTI_PLUGIN_ALC_VALIDATION.md](../docs/MULTI_PLUGIN_ALC_VALIDATION.md)
- Key types: [`PluginLoadContext`](../src/Abstractions/Hosting/PluginLoadContext.cs), [`PluginHandle`](../src/Abstractions/Hosting/PluginHandle.cs)

## Bridge / Runtime Model

The **HostBridge** layer mediates between the host process and the plugin's isolated context.
A plugin author typically extends [`StreamingPlugin<TModule, TSettings>`](../src/Hosting/StreamingPlugin.cs) (the abstract base that wires up DI, settings, and lifecycle) and optionally [`HostBridgeRuntimeCache<TRuntime, TSettings>`](../src/HostBridge/HostBridgeRuntimeCache.cs).

Orchestration and download tracking live in [`HostBridgeDownloadOrchestrator`](../src/HostBridge/HostBridgeDownloadOrchestrator.cs) and [`HostBridgeDownloadTracker`](../src/HostBridge/HostBridgeDownloadTracker.cs).

- Full bridge contract and flow: [PLUGIN_BRIDGE.md](../docs/PLUGIN_BRIDGE.md)
- End-to-end message flow diagrams: [Flow.md](../docs/Flow.md)
- Lifecycle helpers: [`PluginLifecycle`](../src/Hosting/PluginLifecycle.cs)

## Where Things Live

| Area | Directory | Purpose |
|---|---|---|
| **Abstractions** | `src/Abstractions/` | Host-owned contracts (`IPlugin`, `IPluginContext`, manifest types, result types). Shipped as a separate assembly the host loads directly. |
| **Base** | `src/Base/` | Abstract bases for plugin implementations ([`BaseStreamingIndexer<T>`](../src/Base/BaseStreamingIndexer.cs), [`BaseStreamingDownloadClient<T>`](../src/Base/BaseStreamingDownloadClient.cs)). |
| **HostBridge** | `src/HostBridge/` | Runtime bridge — orchestration, download tracking, URI helpers, path-traversal guard. |
| **Hosting** | `src/Hosting/` | Plugin entry point ([`StreamingPlugin`](../src/Hosting/StreamingPlugin.cs)), lifecycle, settings binder. |
| **Providers** | `src/Providers/` | LLM provider integrations (e.g. `ClaudeCode`). |
| **Resilience** | `src/Resilience/` | Backend health caching and retry policies. |
| **Security** | `src/Security/` | Credential management, token protection, sanitization. |
| **Services** | `src/Services/` | Cross-cutting services — authentication, caching, deduplication, download, DRM, HTTP, metadata, quality, validation, and more. |
| **Streaming** | `src/Streaming/` | SSE framing, stream decoders, timeout/cancellation policies. |
| **Testing** | `src/Testing/` | Mock factories for unit-testing plugins. |

- Complete architecture status and module inventory: [ARCHITECTURE_STATUS.md](../docs/ARCHITECTURE_STATUS.md)
- Abstractions surface catalogue: [ABSTRACTIONS.md](../docs/ABSTRACTIONS.md)
