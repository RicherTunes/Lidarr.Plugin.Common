# Telemetry (OpenTelemetry)

This library exposes lightweight tracing and metrics you can opt into. Overhead is negligible when no listeners are registered.

Activity

- Source: "Lidarr.Plugin.Common"
- Spans
  - http.send
    - Tags: http.request.method, url.scheme, net.peer.name, url.path, http.response.status_code, retry.attempt, profile
  - host.gate.wait
    - Tags: net.host, profile

Metrics

- Meter: "Lidarr.Plugin.Common"
- Counters
  - cache.hit — increment on cache hit
  - cache.miss — increment on cache miss
- cache.revalidate — increment when 304 revalidates and TTL is refreshed
  - A `XArrCache: revalidated` header (and legacy `X-Arr-Cache`) is also added to synthetic 200 responses built from cached bodies.
  - retry.count — increment on each retry attempt; attributes: net.host, http.method
  - auth.refreshes — increment when an auth/session refresh succeeds
- UpDownCounter (NET 8+ only)
  - ratelimiter.inflight — +1 on host gate acquire, -1 on release; attribute: net.host

Usage

- Traces: add an ActivityListener or use OpenTelemetry .NET SDK to subscribe to the ActivitySource.
- Metrics: configure OpenTelemetry Metrics and add a MeterListener for the meter name above.

Notes

- No telemetry is emitted unless you wire a listener/exporter.
- Tags/attributes adhere to OpenTelemetry semantic conventions where applicable.
