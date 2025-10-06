# Streaming HTTP Defaults

This SDK applies a small, predictable set of default headers for streaming API requests.

Headers

- Accept: `application/json`
  - Most streaming APIs return JSON for control-plane endpoints.
- Accept-Language: `en-US,en;q=0.9`
  - Provides stable English responses for diagnostics while letting servers fall back as needed.
- User-Agent: `<caller-supplied>` (optional)
  - Set via builder `WithStreamingDefaults(userAgent)` or `AddStandardHeaders(userAgent)`; not set unless you pass a value.

Notes

- We do not set `Accept-Encoding`. The HTTP handler should be configured with `AutomaticDecompression` so the runtime negotiates content encoding.
- Both `StreamingApiRequestBuilder.WithStreamingDefaults` and `HttpClientExtensions.AddStandardHeaders` call a single helper (`StreamingHeaderDefaults`) to stay consistent.

