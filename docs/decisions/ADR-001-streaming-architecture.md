# ADR-001: Streaming Architecture for LLM Providers

**Status:** Accepted
**Date:** 2026-01-30
**Decision Makers:** Project maintainers

## Context

Brainarr supports multiple LLM providers (OpenAI, Gemini, Z.AI GLM, Claude Code CLI). Users expect streaming responses for better UX (tokens appearing as they're generated rather than waiting for complete response).

### Technical Constraint

Lidarr's `NzbDrone.Common.Http.IHttpClient` buffers complete HTTP responses before returning. This means:

- HTTP requests return only after the full response body is received
- No access to the underlying `HttpResponseMessage` stream
- No way to read partial content as it arrives

### Options Considered

| Option | Description | Pros | Cons |
|--------|-------------|------|------|
| **A. Pseudo-streaming** | Decode SSE after full download | Simple, uses existing HTTP client | Misleading UX (no tokens, then burst) |
| **B. Real streaming client** | Use `System.Net.Http` with `ResponseHeadersRead` | True token-by-token UX | Bypasses Lidarr's proxy/cert/rate-limit/interceptors; high regression risk |
| **C. CLI-only streaming** | Stream only for subprocess-based providers | Natural stdout streaming; honest UX | HTTP providers don't stream |

## Decision

**Option C: CLI-only streaming**

- Subprocess-based providers (Claude Code CLI) stream naturally via stdout
- HTTP-based providers (OpenAI, Gemini, Z.AI GLM) use non-streaming request/response
- SSE decoders remain in Common as ready infrastructure for future use

## Consequences

### Positive

- Honest UX: CLI providers show real streaming, HTTP providers don't pretend
- No regression risk: Lidarr's HTTP pipeline (proxy, certs, rate limits) unchanged
- Infrastructure ready: When Lidarr supports streaming HTTP, decoders are tested and available

### Negative

- Feature gap: HTTP providers won't show token-by-token output
- Decoders without consumer: `GeminiStreamDecoder`, `ZaiStreamDecoder` have no active consumer until HTTP streaming lands

### Neutral

- Documentation must clearly state which providers support streaming
- Future "Real HTTP Streaming" epic depends on host-level changes

## Implementation

### What Shipped

**Common library:**
- `OpenAiStreamDecoder` - OpenAI/OpenAI-compatible SSE format
- `GeminiStreamDecoder` - Google Gemini SSE format
- `ZaiStreamDecoder` - Z.AI GLM SSE format (OpenAI-compatible with extensions)
- `StreamingTimeoutPolicy`, `StreamingCancellation` - Timeout management
- 230+ decoder and streaming tests (all passing)

**Brainarr:**
- HTTP providers remain non-streaming
- No streaming scaffolding shipped (removed to avoid dead code)

### Provider Streaming Support Matrix

| Provider | Type | Streaming | Notes |
|----------|------|-----------|-------|
| Claude Code CLI | Subprocess | Yes | Natural stdout streaming |
| OpenAI | HTTP | No | Blocked by IHttpClient buffering |
| Gemini | HTTP | No | Blocked by IHttpClient buffering |
| Z.AI GLM | HTTP | No | Blocked by IHttpClient buffering |
| OpenRouter | HTTP | No | Blocked by IHttpClient buffering |
| DeepSeek | HTTP | No | Blocked by IHttpClient buffering |
| Groq | HTTP | No | Blocked by IHttpClient buffering |
| Ollama | HTTP | No | Blocked by IHttpClient buffering |
| LM Studio | HTTP | No | Blocked by IHttpClient buffering |

## Future Work

### Epic: Real HTTP Streaming

**Blocked by:** Lidarr host-level streaming HTTP support

**Would require:**
- Approved streaming HTTP abstraction that reads Lidarr proxy settings
- Certificate handling passthrough
- Rate limit interceptor compatibility
- Resilience policy streaming variant

**When unblocked:**
- Wire `StreamDecoderRegistry` to map providers to decoders
- Add `IStreamingProvider` interface for opt-in streaming
- Update provider implementations to use streaming execution path

## References

- Common streaming code: `src/Streaming/`
- Decoder tests: `tests/Streaming/`
- Related decision: API-key auth now, subscription research later
