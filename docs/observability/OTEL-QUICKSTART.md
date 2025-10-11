# OpenTelemetry Quickstart (Lidarr.Plugin.Common)

This library already emits Activities and Metrics:
- ActivitySource: `Lidarr.Plugin.Common`
- Metrics (Counter/UpDownCounter): `cache.hit`, `cache.miss`, `cache.revalidate`, `retry.count`, `auth.refreshes`, `ratelimiter.inflight`

By default, these are no-ops unless a listener/exporter is present. Use an env flag to enable host-side wiring.

## Enable in your host (feature‑flagged)

Set an environment variable in your process or service:

- `LPC_OTEL_ENABLE=1` — enable OpenTelemetry wiring

Example host bootstrap (minimal):

```csharp
// Program.cs (host)
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = Host.CreateApplicationBuilder(args);

if (Environment.GetEnvironmentVariable("LPC_OTEL_ENABLE") == "1")
{
    builder.Services.AddOpenTelemetry()
        .WithMetrics(m => m
            .AddMeter("Lidarr.Plugin.Common")
            .AddRuntimeInstrumentation()
            .AddProcessInstrumentation()
            .AddOtlpExporter())
        .WithTracing(t => t
            .AddSource("Lidarr.Plugin.Common")
            .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(0.1)))
            .AddHttpClientInstrumentation()
            .AddOtlpExporter());
}

var app = builder.Build();
await app.RunAsync();
```

Notes:
- Swap `AddOtlpExporter()` for your preferred exporter (Jaeger/Zipkin/Console).
- Adjust the sampler to your needs; 0.1 means 10% of traces.

## Sample Grafana dashboard

A lightweight Grafana dashboard JSON is included at:

- `tools/dashboards/lidarr-plugin-common.grafana.json`

It charts:
- Cache hits/misses/revalidations over time
- Retry counts over time
- In‑flight requests per host
- Auth refresh count

Import this JSON into Grafana (Dashboards → Import) once metrics are scraped by Prometheus or flowed via OTel Collector.

## Instrumentation map

- HTTP send: Activity `http.send` with tags `http.request.method`, `url.path`, `net.peer.name`, `retry.attempt` (when relevant)
- Host gate wait: Activity `host.gate.wait` with tags `net.host`, `profile`
- Cache: Counters `cache.hit`, `cache.miss`, `cache.revalidate` (endpoint tag)
- Resilience: Counter `retry.count` (host and method tags)
- Auth: Counter `auth.refreshes`
- Rate limiting: UpDownCounter `ratelimiter.inflight` (host tag)

That’s it — flip `LPC_OTEL_ENABLE=1`, wire OpenTelemetry in your host, and import the sample dashboard.
