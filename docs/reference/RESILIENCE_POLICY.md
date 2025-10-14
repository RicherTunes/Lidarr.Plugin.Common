# Resilience Policy Reference

`ResiliencePolicy` centralizes retry/backoff, timeouts, and per-host concurrency caps that the HTTP executor honors. Use built-ins or derive a custom policy with `.With(...)` to avoid argument-order bugs.

## Built-in profiles

- `Default` — conservative baseline suitable for most control-plane calls.
- `Search` — lower per-request deadline and retries for fast paging.
- `Lookup` — slightly longer retries for details endpoints.
- `Streaming` — long per-request timeout and lower concurrency for ranged downloads.
- `Authentication` — careful retries; short max backoff.
- `Metadata` — similar to `Lookup` but tuned for lightweight metadata calls.

Each profile has:

- `Name` — profile tag (propagated to gates and telemetry).
- `MaxRetries` — attempts including the first try.
- `RetryBudget` — total wall-clock budget across retries.
- `PerRequestTimeout` — optional hard timeout per HTTP request.
- `MaxConcurrencyPerHost` — gate for this profile+host pair.
- `MaxTotalConcurrencyPerHost` — optional aggregate cap for all profiles to the same host.
- `InitialBackoff`/`MaxBackoff`/`JitterMin`/`JitterMax` — backoff and jitter envelope.

## Choosing a profile

- Search/list endpoints → `ResiliencePolicy.Search`.
- Item details/lookup → `ResiliencePolicy.Lookup`.
- Auth/token exchange → `ResiliencePolicy.Authentication`.
- Long downloads/streaming → `ResiliencePolicy.Streaming`.

## Customizing

```csharp
var tight = ResiliencePolicy.Search.With(
    maxRetries: 3,
    retryBudget: TimeSpan.FromSeconds(12),
    perRequestTimeout: TimeSpan.FromSeconds(8),
    maxConcurrencyPerHost: 4);

using var resp = await http.ExecuteWithResilienceAsync(req, tight, token);
```

## Notes

- Retry-After absolute dates are honored without extra jitter (still clamped by the budget).
- Relative URIs are resolved against `HttpClient.BaseAddress` inside the executor.
- Profile name is stamped as an option and propagates to host gates and spans.

