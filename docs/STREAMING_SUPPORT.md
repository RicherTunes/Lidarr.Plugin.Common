# Streaming Support Matrix

## Overview

This document describes the streaming support status for LLM providers in the Lidarr Plugin Ecosystem.

## Provider Streaming Matrix

| Provider | Type | Streaming | Status | Notes |
|----------|------|-----------|--------|-------|
| **Claude Code CLI** | Subprocess | Yes | Supported | Natural stdout streaming |
| OpenAI | HTTP | No | Deferred | IHttpClient buffers |
| Gemini | HTTP | No | Deferred | IHttpClient buffers |
| Z.AI GLM | HTTP | No | Deferred | IHttpClient buffers |
| OpenRouter | HTTP | No | Deferred | IHttpClient buffers |
| DeepSeek | HTTP | No | Deferred | IHttpClient buffers |
| Groq | HTTP | No | Deferred | IHttpClient buffers |
| Ollama | HTTP | No | Deferred | IHttpClient buffers |
| LM Studio | HTTP | No | Deferred | IHttpClient buffers |

## Technical Background

### Why CLI Providers Can Stream

Subprocess-based providers (like Claude Code CLI) naturally support streaming because:
- stdout is inherently a stream
- Content arrives token-by-token as the subprocess writes
- No buffering at the HTTP layer

### Why HTTP Providers Cannot Stream (Yet)

Lidarr's `NzbDrone.Common.Http.IHttpClient` buffers complete HTTP responses before returning. This means:
- HTTP requests return only after the full response body is received
- No access to the underlying `HttpResponseMessage` stream
- SSE (Server-Sent Events) chunks are buffered, then decoded after download completes

### Infrastructure Ready

The Common library includes SSE decoders ready for future use:
- `OpenAiStreamDecoder` - OpenAI and OpenAI-compatible providers
- `GeminiStreamDecoder` - Google Gemini
- `ZaiStreamDecoder` - Z.AI GLM

These decoders are fully tested (~230 tests passing as of 2026-01-30) and will be activated when HTTP streaming becomes possible.

### Decoder Consumer Status

| Decoder | Consumer | Status | Notes |
|---------|----------|--------|-------|
| `OpenAiStreamDecoder` | Blocked by host (IHttpClient buffers) | **Future/Non-blocking** | [ADR-001](decisions/ADR-001-streaming-architecture.md) |
| `GeminiStreamDecoder` | Blocked by host (IHttpClient buffers) | **Future/Non-blocking** | [ADR-001](decisions/ADR-001-streaming-architecture.md) |
| `ZaiStreamDecoder` | Blocked by host (IHttpClient buffers) | **Future/Non-blocking** | [ADR-001](decisions/ADR-001-streaming-architecture.md) |

*As of 2026-01-30. Decoders are tested infrastructure awaiting host-level streaming support.*

## Future Work

Real HTTP streaming is blocked by Lidarr's `IHttpClient` buffering. When host-level streaming support becomes available, the decoders are ready to enable token-by-token UX for HTTP providers.

See [ADR-001: Streaming Architecture](decisions/ADR-001-streaming-architecture.md) for the full decision record.
