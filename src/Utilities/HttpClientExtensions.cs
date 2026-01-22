using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Utilities;
using Lidarr.Plugin.Common.Services.Deduplication;
using System.ComponentModel;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Extension methods for HttpClient to provide common functionality for streaming service plugins.
    /// </summary>
    public static class HttpClientExtensions
    {

        /// <summary>
        /// Executes an HTTP request with built-in retry logic and error handling.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static async Task<HttpResponseMessage> ExecuteWithRetryAsync(
            this HttpClient httpClient,
            HttpRequestMessage request,
            int maxRetries = 3,
            int initialDelayMs = 1000,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteWithRetryAsync(
                httpClient,
                request,
                HttpCompletionOption.ResponseContentRead,
                maxRetries,
                initialDelayMs,
                cancellationToken);
        }

        /// <summary>
        /// Executes an HTTP request with built-in retry logic and error handling, allowing
        /// the caller to control response buffering behavior via <see cref="HttpCompletionOption"/>.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static async Task<HttpResponseMessage> ExecuteWithRetryAsync(
            this HttpClient httpClient,
            HttpRequestMessage request,
            HttpCompletionOption completionOption,
            int maxRetries = 3,
            int initialDelayMs = 1000,
            CancellationToken cancellationToken = default)
        {
            return await RetryUtilities.ExecuteWithRetryAsync(
                async () =>
                {
                    // Clone the request for retry attempts
                    var clonedRequest = await CloneHttpRequestMessageAsync(request).ConfigureAwait(false);
                    try
                    {
                        var response = await httpClient.SendAsync(clonedRequest, completionOption, cancellationToken).ConfigureAwait(false);

                        if (!response.IsSuccessStatusCode && RetryUtilities.IsRetryableStatusCode(response.StatusCode))
                        {
                            // Dispose the response before throwing so we don't leak connections on retries.
                            response.Dispose();
                            throw new HttpRequestException(
                                $"Retryable HTTP status code {(int)response.StatusCode} ({response.StatusCode})",
                                inner: null,
                                statusCode: response.StatusCode);
                        }

                        return response;
                    }
                    finally
                    {
                        clonedRequest.Dispose();
                    }
                },
                maxRetries,
                initialDelayMs,
                $"HTTP {request.Method} to {request.RequestUri}");
        }

        public static Task<HttpResponseMessage> ExecuteWithResilienceAsync(
            this HttpClient httpClient,
            HttpRequestMessage request,
            int maxRetries,
            TimeSpan? retryBudget,
            int maxConcurrencyPerHost,
            TimeSpan? perRequestTimeout,
            CancellationToken cancellationToken)
        {
            return ExecuteWithResilienceAsyncCore(
                httpClient,
                request,
                maxRetries,
                retryBudget,
                maxConcurrencyPerHost,
                maxConcurrencyPerHost,
                perRequestTimeout,
                cancellationToken);
        }

        internal static Task<HttpResponseMessage> ExecuteWithResilienceAsyncInternal(
            this HttpClient httpClient,
            HttpRequestMessage request,
            int maxRetries,
            TimeSpan? retryBudget,
            int maxConcurrencyPerHost,
            int maxTotalConcurrencyPerHost,
            TimeSpan? perRequestTimeout,
            CancellationToken cancellationToken)
        {
            return ExecuteWithResilienceAsyncCore(
                httpClient,
                request,
                maxRetries,
                retryBudget,
                maxConcurrencyPerHost,
                maxTotalConcurrencyPerHost,
                perRequestTimeout,
                cancellationToken);
        }

        /// <summary>
        /// Overload that accepts a ResiliencePolicy and maps it to the core implementation.
        /// Keeps call sites minimal and avoids argument order mistakes.
        /// </summary>
        public static Task<HttpResponseMessage> ExecuteWithResilienceAsync(
            this HttpClient httpClient,
            HttpRequestMessage request,
            ResiliencePolicy policy,
            CancellationToken cancellationToken = default)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            return ExecuteWithResilienceAsyncCore(
                httpClient,
                request,
                policy.MaxRetries,
                policy.RetryBudget,
                policy.MaxConcurrencyPerHost,
                policy.MaxTotalConcurrencyPerHost,
                policy.PerRequestTimeout,
                cancellationToken);
        }


        public static async Task<HttpResponseMessage> SendWithResilienceAsync(
            this HttpClient httpClient,
            Services.Http.StreamingApiRequestBuilder builder,
            int maxRetries = 5,
            TimeSpan? retryBudget = null,
            int maxConcurrencyPerHost = 6,
            CancellationToken cancellationToken = default)
        {
            if (httpClient == null) throw new ArgumentNullException(nameof(httpClient));
            if (builder == null) throw new ArgumentNullException(nameof(builder));

            var request = builder.Build();
            var info = builder.BuildForLogging();

            try
            {
                // Prefer policy-based execution when the builder specifies it.
                if (builder.Policy != null)
                {
                    return await httpClient.ExecuteWithResilienceAsync(
                        request,
                        builder.Policy,
                        cancellationToken).ConfigureAwait(false);
                }
                // Warn/measure once when callers bypass DI policy path
                try
                {
                    if (Interlocked.Exchange(ref s_nonDiWarned, 1) == 0)
                    {
                        Observability.Metrics.ResilienceNonDI.Add(1);
                    }
                }
                catch { }

                return await httpClient.ExecuteWithResilienceAsync(
                    request,
                    maxRetries,
                    retryBudget,
                    maxConcurrencyPerHost,
                    info.Timeout,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                request.Dispose();
            }
        }

        private static int s_nonDiWarned = 0;

        /// <summary>
        /// Sends a request with resilience and singleflight deduplication for identical GETs.
        /// Deduplication shares a buffered HTTP record (status, headers-of-interest, content bytes) across callers,
        /// and rebuilds a fresh HttpResponseMessage per consumer to avoid shared disposal issues.
        /// </summary>
        public static async Task<HttpResponseMessage> SendWithResilienceAsync(
            this HttpClient httpClient,
            Services.Http.StreamingApiRequestBuilder builder,
            RequestDeduplicator deduplicator,
            int maxRetries = 5,
            TimeSpan? retryBudget = null,
            int maxConcurrencyPerHost = 6,
            CancellationToken cancellationToken = default)
        {
            if (httpClient == null) throw new ArgumentNullException(nameof(httpClient));
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (deduplicator == null) throw new ArgumentNullException(nameof(deduplicator));

            var request = builder.Build();
            var info = builder.BuildForLogging();

            try
            {
                var method = request.Method?.Method?.ToUpperInvariant() ?? "GET";
                // Only dedupe GET-like requests
                if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    // fall back to normal path
                    if (builder.Policy != null)
                    {
                        return await httpClient.ExecuteWithResilienceAsync(request, builder.Policy, cancellationToken).ConfigureAwait(false);
                    }

                    return await httpClient.ExecuteWithResilienceAsync(
                        request,
                        maxRetries,
                        retryBudget,
                        maxConcurrencyPerHost,
                        info.Timeout,
                        cancellationToken).ConfigureAwait(false);
                }

                var key = BuildRequestDedupKey(request);
                var record = await deduplicator.GetOrCreateAsync(key, async () =>
                {
                    // Send once and buffer response into a small immutable record
                    using var resp = builder.Policy != null
                        ? await httpClient.ExecuteWithResilienceAsync(request, builder.Policy, cancellationToken).ConfigureAwait(false)
                        : await httpClient.ExecuteWithResilienceAsync(request, maxRetries, retryBudget, maxConcurrencyPerHost, info.Timeout, cancellationToken).ConfigureAwait(false);

                    var status = (int)resp.StatusCode;
                    var reason = resp.ReasonPhrase;

                    // Snapshot response headers of interest
                    var headerPairs = resp.Headers?.SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v))).ToArray() ?? Array.Empty<KeyValuePair<string, string>>();
                    var contentHeaderPairs = resp.Content?.Headers?.SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v))).ToArray() ?? Array.Empty<KeyValuePair<string, string>>();

                    var bytes = resp.Content != null ? await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false) : Array.Empty<byte>();
                    var mediaType = resp.Content?.Headers?.ContentType?.MediaType;
                    var encoding = resp.Content?.Headers?.ContentType?.CharSet;

                    return new HttpRecord(status, reason, headerPairs, contentHeaderPairs, bytes, mediaType, encoding);
                }, cancellationToken).ConfigureAwait(false);

                // Rebuild a fresh HttpResponseMessage per consumer
                var rebuilt = new HttpResponseMessage((HttpStatusCode)record.StatusCode)
                {
                    ReasonPhrase = record.ReasonPhrase,
                    Content = new ByteArrayContent(record.ContentBytes)
                };

                foreach (var h in record.Headers)
                {
                    rebuilt.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
                foreach (var ch in record.ContentHeaders)
                {
                    rebuilt.Content.Headers.TryAddWithoutValidation(ch.Key, ch.Value);
                }

                if (!string.IsNullOrWhiteSpace(record.MediaType))
                {
                    rebuilt.Content.Headers.ContentType = new MediaTypeHeaderValue(record.MediaType);
                    if (!string.IsNullOrWhiteSpace(record.Charset))
                    {
                        rebuilt.Content.Headers.ContentType.CharSet = record.Charset;
                    }
                }

                return rebuilt;
            }
            finally
            {
                request.Dispose();
            }
        }

        private readonly record struct HttpRecord(
            int StatusCode,
            string? ReasonPhrase,
            KeyValuePair<string, string>[] Headers,
            KeyValuePair<string, string>[] ContentHeaders,
            byte[] ContentBytes,
            string? MediaType,
            string? Charset);
        private static async Task<HttpResponseMessage> ExecuteWithResilienceAsyncCore(
            HttpClient httpClient,
            HttpRequestMessage request,
            int maxRetries,
            TimeSpan? retryBudget,
            int maxConcurrencyPerHost,
            int maxTotalConcurrencyPerHost,
            TimeSpan? perRequestTimeout,
            CancellationToken cancellationToken)
        {
            if (httpClient == null) throw new ArgumentNullException(nameof(httpClient));
            if (request == null) throw new ArgumentNullException(nameof(request));

            retryBudget ??= TimeSpan.FromSeconds(60);
            using var timeoutCts = perRequestTimeout.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            if (perRequestTimeout.HasValue)
            {
                timeoutCts!.CancelAfter(perRequestTimeout.Value);
            }

            var effectiveToken = timeoutCts?.Token ?? cancellationToken;
            var deadline = DateTime.UtcNow + retryBudget.Value;
            var attempt = 0;
            // Resolve relative URIs against HttpClient.BaseAddress when present
            Uri? currentUri = request.RequestUri;
            if (currentUri != null && !currentUri.IsAbsoluteUri)
            {
                if (httpClient.BaseAddress == null)
                {
                    throw new InvalidOperationException("Relative RequestUri without HttpClient.BaseAddress.");
                }
                currentUri = new Uri(httpClient.BaseAddress, currentUri);
            }

            var host = currentUri?.Host;
            string hostKey = host ?? "__unknown__";
            string profileTag = "default";
            try
            {
                if (request.Options.TryGetValue(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.ProfileKey, out string? profile) && !string.IsNullOrWhiteSpace(profile))
                {
                    hostKey = hostKey + "|" + profile;
                    profileTag = profile;
                }
            }
            catch { }

            var gate = HostGateRegistry.Get(hostKey, Math.Max(1, maxConcurrencyPerHost));
            var aggregateEffective = Math.Max(1, maxTotalConcurrencyPerHost);
            var aggregateGate = HostGateRegistry.GetAggregate(host, aggregateEffective);

            using (var waitActivity = Observability.Activity.StartActivity("host.gate.wait", ActivityKind.Internal))
            {
                waitActivity?.SetTag("net.host", host ?? "__unknown__");
                waitActivity?.SetTag("profile", profileTag);
                await aggregateGate.WaitAsync(effectiveToken).ConfigureAwait(false);
                await gate.WaitAsync(effectiveToken).ConfigureAwait(false);
            }

            // Track inflight per host (approximate). Increment on acquire; decrement on release.
            try {
#if NET8_0_OR_GREATER
                Observability.Metrics.RateLimiterInflight.Add(1, new KeyValuePair<string, object?>("net.host", host ?? "__unknown__"));
#endif
            } catch { }
            try
            {
                while (true)
                {
                    attempt++;

                    using var attemptRequest = await CloneForRetryAsync(request).ConfigureAwait(false);
                    if (currentUri != null) { attemptRequest.RequestUri = currentUri; }

                    HttpResponseMessage response;
                    using var httpActivity = Observability.Activity.StartActivity("http.send", ActivityKind.Client);
                    if (httpActivity != null)
                    {
                        try
                        {
                            httpActivity.SetTag("http.request.method", attemptRequest.Method.Method);
                            if (attemptRequest.RequestUri != null)
                            {
                                httpActivity.SetTag("url.scheme", attemptRequest.RequestUri.Scheme);
                                httpActivity.SetTag("net.peer.name", attemptRequest.RequestUri.Host);
                                httpActivity.SetTag("url.path", attemptRequest.RequestUri.AbsolutePath);
                            }
                            httpActivity.SetTag("retry.attempt", attempt);
                            httpActivity.SetTag("profile", profileTag);
                        }
                        catch { }
                    }
                    try
                    {
                        response = await httpClient.SendAsync(
                                attemptRequest,
                                HttpCompletionOption.ResponseHeadersRead,
                                effectiveToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex) when (perRequestTimeout.HasValue &&
                                                               timeoutCts!.IsCancellationRequested &&
                                                               !cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"HTTP request to {request.RequestUri} exceeded the per-request timeout of {perRequestTimeout.Value}.",
                            ex);
                    }

                    if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 300)
                    {
                        try
                        {
                            httpActivity?.SetTag("http.response.status_code", (int)response.StatusCode);
                        }
                        catch { }
                        return response;
                    }

                    var status = (int)response.StatusCode;
                    // Do not retry non-429 4xx. Only 429 and 5xx are retryable.
                    var retryable = status == 429 || (status >= 500 && status <= 599);
                    if (!retryable || attempt >= maxRetries)
                    {
                        try
                        {
                            httpActivity?.SetTag("http.response.status_code", status);
                            httpActivity?.SetTag("resilience.retryable", retryable);
                        }
                        catch { }
                        // Handle 307/308 redirects preserving method & body
                        if ((status == 307 || status == 308) && response.Headers.Location != null)
                        {
                            try
                            {
                                var loc = response.Headers.Location;
                                var targetUri = loc.IsAbsoluteUri ? loc : new Uri(attemptRequest.RequestUri!, loc);

                                // If the redirect crosses hosts, release current gates and reacquire for the new host
                                var newHost = targetUri.Host;
                                var crossingHosts = !string.Equals(newHost, host, StringComparison.OrdinalIgnoreCase);

                                if (crossingHosts)
                                {
                                    try
                                    {
#if NET8_0_OR_GREATER
                                        // Decrement inflight for old host before moving
                                        Observability.Metrics.RateLimiterInflight.Add(-1, new KeyValuePair<string, object?>("net.host", host ?? "__unknown__"));
#endif
                                    }
                                    catch { }

                                    // Release current gates before acquiring new ones
                                    try { gate.Release(); } catch { }
                                    try { aggregateGate.Release(); } catch { }

                                    host = newHost;
                                    hostKey = host ?? "__unknown__";
                                    if (!string.IsNullOrWhiteSpace(profileTag)) hostKey = hostKey + "|" + profileTag;

                                    // Acquire new gates for redirected host
                                    var newAggregate = HostGateRegistry.GetAggregate(host, aggregateEffective);
                                    await newAggregate.WaitAsync(effectiveToken).ConfigureAwait(false);
                                    var newGate = HostGateRegistry.Get(hostKey, Math.Max(1, maxConcurrencyPerHost));
                                    await newGate.WaitAsync(effectiveToken).ConfigureAwait(false);

                                    // Swap references so finalizer releases the currently-held gates
                                    aggregateGate = newAggregate;
                                    gate = newGate;

                                    try
                                    {
#if NET8_0_OR_GREATER
                                        // Increment inflight for new host
                                        Observability.Metrics.RateLimiterInflight.Add(1, new KeyValuePair<string, object?>("net.host", host ?? "__unknown__"));
#endif
                                    }
                                    catch { }
                                }

                                currentUri = targetUri;
                                response.Dispose();
                                // Continue immediately without backoff; do not count against retry budget
                                continue;
                            }
                            catch { /* fall through to return */ }
                        }
                        // Handle 301/302 redirects only when safe (GET/HEAD). Do not auto-follow for unsafe methods (e.g., POST)
                        else if ((status == 301 || status == 302) && response.Headers.Location != null &&
                                 (HttpMethod.Get.Equals(attemptRequest.Method) || HttpMethod.Head.Equals(attemptRequest.Method)))
                        {
                            try
                            {
                                var loc = response.Headers.Location;
                                var targetUri = loc.IsAbsoluteUri ? loc : new Uri(attemptRequest.RequestUri!, loc);

                                // If the redirect crosses hosts, release current gates and reacquire for the new host
                                var newHost = targetUri.Host;
                                var crossingHosts = !string.Equals(newHost, host, StringComparison.OrdinalIgnoreCase);

                                if (crossingHosts)
                                {
                                    try
                                    {
#if NET8_0_OR_GREATER
                                        Observability.Metrics.RateLimiterInflight.Add(-1, new KeyValuePair<string, object?>("net.host", host ?? "__unknown__"));
#endif
                                    }
                                    catch { }

                                    try { gate.Release(); } catch { }
                                    try { aggregateGate.Release(); } catch { }

                                    host = newHost;
                                    hostKey = host ?? "__unknown__";
                                    if (!string.IsNullOrWhiteSpace(profileTag)) hostKey = hostKey + "|" + profileTag;

                                    var newAggregate = HostGateRegistry.GetAggregate(host, aggregateEffective);
                                    await newAggregate.WaitAsync(effectiveToken).ConfigureAwait(false);
                                    var newGate = HostGateRegistry.Get(hostKey, Math.Max(1, maxConcurrencyPerHost));
                                    await newGate.WaitAsync(effectiveToken).ConfigureAwait(false);

                                    aggregateGate = newAggregate;
                                    gate = newGate;

                                    try
                                    {
#if NET8_0_OR_GREATER
                                        Observability.Metrics.RateLimiterInflight.Add(1, new KeyValuePair<string, object?>("net.host", host ?? "__unknown__"));
#endif
                                    }
                                    catch { }
                                }

                                currentUri = targetUri;
                                response.Dispose();
                                // Continue without backoff; redirect handling should not consume retry budget
                                continue;
                            }
                            catch { /* fall through to return */ }
                        }
                        return response;
                    }

                    var now = DateTime.UtcNow;
                    // Prefer Retry-After absolute date over delta; do not add jitter when an absolute date is provided
                    TimeSpan delay;
                    var preferred = GetRetryDelayPreferredDate(response);
                    if (preferred.HasValue)
                    {
                        delay = preferred.Value;
                    }
                    else
                    {
                        delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))) + GetJitter();
                    }
                    var remaining = deadline - now;
                    if (remaining <= TimeSpan.Zero)
                    {
                        try { httpActivity?.SetTag("resilience.deadline.exhausted", true); } catch { }
                        return response;
                    }
                    if (delay > remaining)
                    {
                        delay = remaining; // clamp to budget
                    }

                    try
                    {
                        Observability.Metrics.RetryCount.Add(1,
                            new KeyValuePair<string, object?>("net.host", host ?? "__unknown__"),
                            new KeyValuePair<string, object?>("http.method", request.Method.Method));
                        httpActivity?.AddEvent(new ActivityEvent("retry", tags: new ActivityTagsCollection
                        {
                            { "retry.delay.ms", (long)delay.TotalMilliseconds },
                            { "retry.reason", status }
                        }));
                    }
                    catch { }
                    response.Dispose();
                    await Task.Delay(delay, effectiveToken).ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
                aggregateGate.Release();
                try {
#if NET8_0_OR_GREATER
                    Observability.Metrics.RateLimiterInflight.Add(-1, new KeyValuePair<string, object?>("net.host", host ?? "__unknown__"));
#endif
                } catch { }
            }
        }

// ---- TimeProvider-enabled variants (net8+) ----
#if NET8_0_OR_GREATER
        public static Task<HttpResponseMessage> ExecuteWithResilienceAsync(
            this HttpClient httpClient,
            HttpRequestMessage request,
            int maxRetries,
            TimeSpan? retryBudget,
            int maxConcurrencyPerHost,
            TimeSpan? perRequestTimeout,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            if (timeProvider == null) throw new ArgumentNullException(nameof(timeProvider));
            return ExecuteWithResilienceAsyncCore(
                httpClient,
                request,
                maxRetries,
                retryBudget,
                maxConcurrencyPerHost,
                maxConcurrencyPerHost,
                perRequestTimeout,
                timeProvider,
                cancellationToken);
        }

        private static async Task<HttpResponseMessage> ExecuteWithResilienceAsyncCore(
            HttpClient httpClient,
            HttpRequestMessage request,
            int maxRetries,
            TimeSpan? retryBudget,
            int maxConcurrencyPerHost,
            int maxTotalConcurrencyPerHost,
            TimeSpan? perRequestTimeout,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            if (httpClient == null) throw new ArgumentNullException(nameof(httpClient));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (timeProvider == null) throw new ArgumentNullException(nameof(timeProvider));

            retryBudget ??= TimeSpan.FromSeconds(60);
            using var timeoutCts = perRequestTimeout.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            if (perRequestTimeout.HasValue)
            {
                timeoutCts!.CancelAfter(perRequestTimeout.Value);
            }

            var effectiveToken = timeoutCts?.Token ?? cancellationToken;
            var deadline = timeProvider.GetUtcNow().UtcDateTime + retryBudget.Value;
            var attempt = 0;

            Uri? currentUri = request.RequestUri;
            if (currentUri != null && !currentUri.IsAbsoluteUri)
            {
                if (httpClient.BaseAddress == null)
                {
                    throw new InvalidOperationException("Relative RequestUri without HttpClient.BaseAddress.");
                }
                currentUri = new Uri(httpClient.BaseAddress, currentUri);
            }

            var host = currentUri?.Host;
            string hostKey = host ?? "__unknown__";
            string profileTag = "default";
            try
            {
                if (request.Options.TryGetValue(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.ProfileKey, out string? profile) && !string.IsNullOrWhiteSpace(profile))
                {
                    hostKey = hostKey + "|" + profile;
                    profileTag = profile;
                }
            }
            catch { }

            var gate = HostGateRegistry.Get(hostKey, Math.Max(1, maxConcurrencyPerHost));
            var aggregateEffective = Math.Max(1, maxTotalConcurrencyPerHost);
            var aggregateGate = HostGateRegistry.GetAggregate(host, aggregateEffective);

            using (var waitActivity = Observability.Activity.StartActivity("host.gate.wait", ActivityKind.Internal))
            {
                waitActivity?.SetTag("net.host", host ?? "__unknown__");
                waitActivity?.SetTag("profile", profileTag);
                await aggregateGate.WaitAsync(effectiveToken).ConfigureAwait(false);
                await gate.WaitAsync(effectiveToken).ConfigureAwait(false);
            }

            try { Observability.Metrics.RateLimiterInflight.Add(1, new KeyValuePair<string, object?>("net.host", host ?? "__unknown__")); } catch { }
            try
            {
                while (true)
                {
                    attempt++;

                    using var attemptRequest = await CloneForRetryAsync(request).ConfigureAwait(false);
                    if (currentUri != null) { attemptRequest.RequestUri = currentUri; }

                    HttpResponseMessage response;
                    using var httpActivity = Observability.Activity.StartActivity("http.send", ActivityKind.Client);
                    if (httpActivity != null)
                    {
                        try
                        {
                            httpActivity.SetTag("http.request.method", attemptRequest.Method.Method);
                            if (attemptRequest.RequestUri != null)
                            {
                                httpActivity.SetTag("url.scheme", attemptRequest.RequestUri.Scheme);
                                httpActivity.SetTag("net.peer.name", attemptRequest.RequestUri.Host);
                                httpActivity.SetTag("url.path", attemptRequest.RequestUri.AbsolutePath);
                            }
                            httpActivity.SetTag("retry.attempt", attempt);
                            httpActivity.SetTag("profile", profileTag);
                        }
                        catch { }
                    }
                    try
                    {
                        response = await httpClient.SendAsync(
                                attemptRequest,
                                HttpCompletionOption.ResponseHeadersRead,
                                effectiveToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex) when (perRequestTimeout.HasValue &&
                                                               timeoutCts!.IsCancellationRequested &&
                                                               !cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"Request exceeded the per-request timeout of {perRequestTimeout.Value}.",
                            ex);
                    }

                    var status = (int)response.StatusCode;
                    var retryable = status == (int)HttpStatusCode.RequestTimeout
                                   || status == (int)HttpStatusCode.TooManyRequests
                                   || (status >= 500 && status <= 599);

                    if (!retryable || attempt >= maxRetries)
                    {
                        return response;
                    }

                    TimeSpan delay;
                    var preferred = GetRetryDelayPreferredDate(response, timeProvider);
                    if (preferred.HasValue)
                    {
                        delay = preferred.Value;
                    }
                    else
                    {
                        delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))) + GetJitter();
                    }

                    var now = timeProvider.GetUtcNow().UtcDateTime;
                    var remaining = deadline - now;
                    if (remaining <= TimeSpan.Zero)
                    {
                        try { httpActivity?.SetTag("resilience.deadline.exhausted", true); } catch { }
                        return response;
                    }
                    if (delay > remaining)
                    {
                        delay = remaining; // clamp to budget
                    }

                    try
                    {
                        Observability.Metrics.RetryCount.Add(1,
                            new KeyValuePair<string, object?>("net.host", host ?? "__unknown__"),
                            new KeyValuePair<string, object?>("http.method", request.Method.Method));
                        httpActivity?.AddEvent(new ActivityEvent("retry", tags: new ActivityTagsCollection
                        {
                            { "retry.delay.ms", (long)delay.TotalMilliseconds },
                            { "retry.reason", status }
                        }));
                    }
                    catch { }
                    response.Dispose();
                    await DelayAsync(delay, timeProvider, effectiveToken).ConfigureAwait(false);
                }
            }
            finally
            {
                try { gate.Release(); } catch { }
                try { aggregateGate.Release(); } catch { }
                try { Observability.Metrics.RateLimiterInflight.Add(-1, new KeyValuePair<string, object?>("net.host", host ?? "__unknown__")); } catch { }
            }
        }

        private static TimeSpan? GetRetryDelayPreferredDate(HttpResponseMessage response, TimeProvider timeProvider)
        {
            try
            {
                var ra = response.Headers?.RetryAfter;
                if (ra == null) return null;

                if (ra.Date.HasValue)
                {
                    var delta = ra.Date.Value - timeProvider.GetUtcNow();
                    if (delta > TimeSpan.Zero) return delta;
                }
                if (ra.Delta.HasValue) return ra.Delta.Value;
            }
            catch { }
            return null;
        }

        private static async Task DelayAsync(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)
        {
            if (delay <= TimeSpan.Zero) return;
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var ctr = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            using var timer = timeProvider.CreateTimer(static s => ((TaskCompletionSource<object?>)s!).TrySetResult(null), tcs, delay, Timeout.InfiniteTimeSpan);
            await tcs.Task.ConfigureAwait(false);
        }
#endif

        /// <summary>
        /// Executes an HTTP request and deserializes the JSON response.
        /// </summary>
        public static async Task<T> GetJsonAsync<T>(
            this HttpClient httpClient,
            string url,
            JsonSerializerOptions options = null,
            CancellationToken cancellationToken = default)
        {
            var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType;
            var payload = await HttpContentLightUp.ReadAsStringAsync(response.Content, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(payload))
            {
                return default;
            }

            if (!IsJsonMediaType(contentType))
            {
                var previewLength = Math.Min(8, payload.Length);
                var previewSegment = payload.Substring(0, previewLength);
                var previewBytes = Encoding.UTF8.GetBytes(previewSegment);
                var previewHex = previewLength > 0 ? BitConverter.ToString(previewBytes) : "<empty>";
                var statusCode = (int)response.StatusCode;
                throw new InvalidOperationException($"Expected JSON but got '{contentType ?? "none"}' from {response.RequestMessage?.RequestUri} (status {statusCode}) (first bytes: {previewHex}).");
            }

            options ??= new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var result = JsonSerializer.Deserialize<T>(payload, options);
            if (result == null)
            {
                throw new InvalidOperationException("Failed to deserialize JSON payload into the requested type.");
            }

            return result;
        }

        /// <summary>
        /// Posts JSON data and returns a deserialized response.
        /// </summary>
        public static async Task<TResponse> PostJsonAsync<TRequest, TResponse>(
            this HttpClient httpClient,
            string url,
            TRequest data,
            JsonSerializerOptions options = null,
            CancellationToken cancellationToken = default)
        {
            var json = JsonSerializer.Serialize(data, options);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(url, content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<TResponse>(responseContent, options);
        }

        /// <summary>
        /// Builds a log-safe URL that includes only query parameter names (no values).
        /// This avoids leaking secrets embedded in query parameter values.
        /// </summary>
        internal static string BuildUrlWithQueryNames(string baseUrl, IEnumerable<KeyValuePair<string, string>> parameters)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return baseUrl ?? string.Empty;
            }

            var url = baseUrl;

            string fragment = string.Empty;
            var fragmentIndex = url.IndexOf('#');
            if (fragmentIndex >= 0)
            {
                fragment = url.Substring(fragmentIndex);
                url = url.Substring(0, fragmentIndex);
            }

            string existingQuery = string.Empty;
            var queryIndex = url.IndexOf('?');
            if (queryIndex >= 0)
            {
                existingQuery = url.Substring(queryIndex + 1);
                url = url.Substring(0, queryIndex);
            }

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(existingQuery))
            {
                foreach (var part in existingQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var key = part;
                    var eq = part.IndexOf('=');
                    if (eq >= 0)
                    {
                        key = part.Substring(0, eq);
                    }

                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    // Keys may be URL-encoded; normalize best-effort for stable output.
                    try
                    {
                        key = Uri.UnescapeDataString(key);
                    }
                    catch
                    {
                        // Ignore invalid escapes and keep the raw key.
                    }

                    keys.Add(key);
                }
            }

            if (parameters != null)
            {
                foreach (var p in parameters)
                {
                    if (!string.IsNullOrWhiteSpace(p.Key))
                    {
                        keys.Add(p.Key);
                    }
                }
            }

            if (keys.Count == 0)
            {
                return url + fragment;
            }

            var queryString = string.Join("&", keys
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .Select(Uri.EscapeDataString));

            return $"{url}?{queryString}{fragment}";
        }

        /// <summary>
        /// Builds a URL with query parameters from a dictionary.
        /// </summary>
        public static string BuildUrlWithParams(string baseUrl, Dictionary<string, string> parameters)
        {
            if (parameters == null || !parameters.Any())
                return baseUrl;

            var queryString = string.Join("&",
                parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

            var separator = baseUrl.Contains('?') ? "&" : "?";
            return $"{baseUrl}{separator}{queryString}";
        }

        /// <summary>
        /// Builds a URL with query parameters from a sequence of pairs (supports multivalue keys).
        /// </summary>
        public static string BuildUrlWithParams(string baseUrl, IEnumerable<KeyValuePair<string, string>> parameters)
        {
            if (parameters == null)
                return baseUrl;

            var list = parameters as IList<KeyValuePair<string, string>> ?? parameters.ToList();
            if (list.Count == 0) return baseUrl;

            var queryString = string.Join("&",
                list.Select(p => $"{Uri.EscapeDataString(p.Key ?? string.Empty)}={Uri.EscapeDataString(p.Value ?? string.Empty)}"));

            var separator = baseUrl.Contains('?') ? "&" : "?";
            return $"{baseUrl}{separator}{queryString}";
        }

        /// <summary>
        /// Adds standard headers for streaming service API calls.
        /// </summary>
        public static HttpRequestMessage AddStandardHeaders(
            this HttpRequestMessage request,
            string userAgent = null,
            Dictionary<string, string> additionalHeaders = null)
        {
            Lidarr.Plugin.Common.Services.Http.StreamingHeaderDefaults.ApplyTo(request, userAgent);

            if (additionalHeaders != null)
            {
                foreach (var header in additionalHeaders)
                {
                    request.Headers.Add(header.Key, header.Value);
                }
            }

            return request;
        }

        /// <summary>
        /// Measures execution time of an HTTP request.
        /// </summary>
        public static async Task<(HttpResponseMessage Response, TimeSpan Duration)> ExecuteWithTimingAsync(
            this HttpClient httpClient,
            HttpRequestMessage request,
            CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await httpClient.SendAsync(request, cancellationToken);
                stopwatch.Stop();
                return (response, stopwatch.Elapsed);
            }
            catch
            {
                stopwatch.Stop();
                throw;
            }
        }

        /// <summary>
        /// Safe method to read response content with encoding detection.
        /// </summary>
        public static async Task<string> ReadContentSafelyAsync(this HttpContent content)
        {
            try
            {
                return await content.ReadAsStringAsync();
            }
            catch (Exception)
            {
                // Fallback to byte reading if string reading fails
                var bytes = await content.ReadAsByteArrayAsync();
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
        }

        /// <summary>
        /// Validates if the response content type is JSON.
        /// </summary>
        public static bool IsJsonContent(this HttpResponseMessage response)
        {
            var contentType = response.Content?.Headers?.ContentType?.MediaType;
            return IsJsonMediaType(contentType);
        }

        /// <summary>
        /// Creates a safe copy of sensitive parameters for logging.
        /// </summary>
        public static Dictionary<string, string> MaskSensitiveParams(Dictionary<string, string> parameters)
        {
            if (parameters == null) return new Dictionary<string, string>();

            var maskedParams = new Dictionary<string, string>();
            foreach (var param in parameters)
            {
                if (IsSensitiveParameter(param.Key) || SensitiveKeys.LooksSensitiveValue(param.Value))
                {
                    maskedParams[param.Key] = MaskValue(param.Value);
                }
                else
                {
                    maskedParams[param.Key] = param.Value;
                }
            }
            return maskedParams;
        }

        /// <summary>
        /// Builds a stable dedup key for GET requests based on request metadata.
        /// Includes authority, endpoint, canonical query parameters, optional auth scope, and profile.
        /// </summary>
        public static string BuildRequestDedupKey(HttpRequestMessage request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var method = request.Method?.Method?.ToUpperInvariant() ?? "GET";
            var authority = request.RequestUri != null
                ? ($"{(request.RequestUri.Scheme ?? "http").ToLowerInvariant()}://{request.RequestUri.Host.ToLowerInvariant()}{(request.RequestUri.IsDefaultPort ? string.Empty : ":" + request.RequestUri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))}")
                : "";

            string endpoint = string.Empty;
            string profile = string.Empty;
            string canonical = string.Empty;
            string scope = string.Empty;

            try
            {
                if (request.Options.TryGetValue(Services.Http.PluginHttpOptions.EndpointKey, out string? ep) && ep != null)
                    endpoint = ep;
            }
            catch { }
            try
            {
                if (request.Options.TryGetValue(Services.Http.PluginHttpOptions.ProfileKey, out string? pr) && pr != null)
                    profile = pr;
            }
            catch { }
            try
            {
                if (request.Options.TryGetValue(Services.Http.PluginHttpOptions.ParametersKey, out string? ca) && ca != null)
                    canonical = ca;
            }
            catch { }
            try
            {
                if (request.Options.TryGetValue(Services.Http.PluginHttpOptions.AuthScopeKey, out string? sc) && sc != null)
                    scope = sc;
            }
            catch { }

            var parts = new[] { method, authority, endpoint, canonical, scope, profile };
            return HashingUtility.ComputeSHA256(string.Join("\n", parts));
        }

        private static bool IsSensitiveParameter(string parameterName)
        {
            return SensitiveKeys.IsSensitive(parameterName);
        }
        private static string MaskValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "[empty]";
            return "[redacted]";
        }

        public static async Task<HttpRequestMessage> CloneHttpRequestMessageAsync(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version
            };

            clone.VersionPolicy = request.VersionPolicy;
            CopyHttpRequestOptions(request, clone);
            // Copy headers
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            // Copy content if present
            if (request.Content != null)
            {
                var contentBytes = await request.Content.ReadAsByteArrayAsync();
                clone.Content = new ByteArrayContent(contentBytes);

                // Copy content headers
                foreach (var header in request.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return clone;
        }

        private static readonly MethodInfo HttpRequestOptionsSetMethod = typeof(System.Net.Http.HttpRequestOptions)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == "Set" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2);

        private static void CopyHttpRequestOptions(HttpRequestMessage source, HttpRequestMessage destination)
        {
            foreach (var option in source.Options)
            {
                var value = option.Value;
                if (value is null)
                {
                    destination.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), null);
                    continue;
                }

                var valueType = value.GetType();
                var keyType = typeof(HttpRequestOptionsKey<>).MakeGenericType(valueType);
                var keyInstance = Activator.CreateInstance(keyType, option.Key);
                var setMethod = HttpRequestOptionsSetMethod.MakeGenericMethod(valueType);
                setMethod.Invoke(destination.Options, new[] { keyInstance, value });
            }
        }
        private static TimeSpan? GetRetryDelayPreferredDate(HttpResponseMessage response)
        {
            try
            {
                var ra = response.Headers?.RetryAfter;
                if (ra == null) return null;

                // Prefer absolute date when present
                if (ra.Date.HasValue)
                {
                    var delta = ra.Date.Value - DateTimeOffset.UtcNow;
                    if (delta > TimeSpan.Zero) return delta;
                }
                if (ra.Delta.HasValue) return ra.Delta.Value;
            }
            catch { /* ignore parse issues */ }
            return null;
        }

        /// <summary>
        /// Clone a request for retry, buffering the original content once and reusing across attempts.
        /// </summary>
        public static async Task<HttpRequestMessage> CloneForRetryAsync(HttpRequestMessage request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
                VersionPolicy = request.VersionPolicy
            };

            CopyHttpRequestOptions(request, clone);

            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content != null)
            {
                // Buffer once into request.Options, then reuse
                byte[]? bodyBytes = null;
                try
                {
                    if (!request.Options.TryGetValue(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.BufferedBodyKey, out bodyBytes) || bodyBytes == null)
                    {
                        bodyBytes = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        request.Options.Set(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.BufferedBodyKey, bodyBytes);
                    }
                }
                catch
                {
                    // As a last resort, fall back to reading the stream fresh
                    bodyBytes = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }

                clone.Content = new ByteArrayContent(bodyBytes ?? Array.Empty<byte>());
                foreach (var header in request.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return clone;
        }

        private static TimeSpan GetJitter()
        {
            var ms = RandomProvider.Next(50, 250);
            return TimeSpan.FromMilliseconds(ms);
        }

        private static bool IsJsonMediaType(string? contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
            {
                return false;
            }

            return contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("text/json", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("application/problem+json", StringComparison.OrdinalIgnoreCase);
        }
    }
}







