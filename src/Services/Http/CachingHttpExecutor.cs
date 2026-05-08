using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Caching;
using Lidarr.Plugin.Common.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Single integrated executor for cache-aware, conditional, resilient HTTP GETs across Arr streaming plugins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type unifies the ~370-line <c>SendAsync</c> body that previously lived inside applemusicarr's
    /// <c>AppleMusicApiClient</c>, qobuzarr's <c>QobuzHttpClient</c>, and brainarr's caching stack. It composes:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>conditional headers (If-None-Match, If-Modified-Since) attached from cache validators or an external <see cref="IConditionalRequestState"/>;</description></item>
    ///   <item><description>soft-window revalidation (return cached body without origin contact while inside <see cref="CachePolicy.SoftRevalidateWindow"/>);</description></item>
    ///   <item><description>resilience via <see cref="GenericResilienceExecutor"/> (429/Retry-After, exponential backoff, retry budget, per-host concurrency gate);</description></item>
    ///   <item><description>304 Not Modified fold (synthesize a 200 OK from the cached body, refresh TTL);</description></item>
    ///   <item><description>2xx caching (buffer once, write to cache + persist validators);</description></item>
    ///   <item><description>stale-if-error (5xx falls back to a cached body within <see cref="CachePolicy.StaleIfErrorTtl"/>);</description></item>
    ///   <item><description>terminal eviction (404/410 evicts validators + cache entry when <see cref="CachePolicy.EvictOnTerminalStatus"/> is true).</description></item>
    /// </list>
    /// <para>
    /// The executor is single-tier — it consumes whatever <see cref="IStreamingResponseCache"/> the plugin provides
    /// (memory, file, or a SmartCache wrapper). Multi-tier behavior is a cache-implementation concern, not an
    /// executor concern.
    /// </para>
    /// </remarks>
    public sealed class CachingHttpExecutor
    {
        private readonly HttpMessageInvoker _invoker;
        private readonly IStreamingResponseCache _cache;
        private readonly ICachePolicyProvider? _policyProvider;
        private readonly ResiliencePolicy _resiliencePolicy;
        private readonly IConditionalRequestState? _conditionalState;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<CachingHttpExecutor> _logger;

        /// <summary>
        /// Creates a new executor. <paramref name="invoker"/> is typically an <see cref="HttpClient"/> or a
        /// <see cref="DelegatingHandler"/>-wrapped invoker; <see cref="HttpClient"/> derives from
        /// <see cref="HttpMessageInvoker"/>.
        /// </summary>
        /// <param name="invoker">HTTP transport that will receive the request after all caching/conditional logic runs.</param>
        /// <param name="cache">Backing response cache. Required.</param>
        /// <param name="resiliencePolicy">
        /// Resilience policy applied via <see cref="GenericResilienceExecutor"/>. When <see langword="null"/>,
        /// <see cref="ResiliencePolicy.Default"/> is used.
        /// </param>
        /// <param name="policyProvider">
        /// Optional policy provider. When supplied, the SendAsync overload that omits a per-call
        /// <see cref="CachePolicy"/> resolves the policy from this provider.
        /// </param>
        /// <param name="conditionalState">Optional external validator store. Falls back to in-cache validators when null.</param>
        /// <param name="timeProvider">Time source — used for soft-revalidate, stale-if-error, and the resilience executor.</param>
        /// <param name="logger">Optional logger. Defaults to a no-op logger.</param>
        public CachingHttpExecutor(
            HttpMessageInvoker invoker,
            IStreamingResponseCache cache,
            ResiliencePolicy? resiliencePolicy = null,
            ICachePolicyProvider? policyProvider = null,
            IConditionalRequestState? conditionalState = null,
            TimeProvider? timeProvider = null,
            ILogger<CachingHttpExecutor>? logger = null)
        {
            _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _resiliencePolicy = resiliencePolicy ?? ResiliencePolicy.Default;
            _policyProvider = policyProvider;
            _conditionalState = conditionalState;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _logger = logger ?? NullLogger<CachingHttpExecutor>.Instance;
        }

        /// <summary>
        /// Non-generic convenience overload — equivalent to calling <see cref="SendAsync{TPayload}(StreamingApiRequestBuilder, CacheKey, CachePolicy, CachingHttpHooks{TPayload}?, CancellationToken)"/>
        /// with <c>TPayload = object?</c> and no parse hook. Use this when the caller wants raw bytes only
        /// (the most common pattern in applemusicarr/qobuzarr).
        /// </summary>
        public Task<CachedHttpResponse<object?>> SendAsync(
            StreamingApiRequestBuilder builder,
            CacheKey key,
            CachePolicy policy,
            CancellationToken cancellationToken = default)
            => SendAsync<object?>(builder, key, policy, hooks: null, cancellationToken);

        /// <summary>
        /// Builds a request from <paramref name="builder"/> and sends it through the cache+resilience pipeline.
        /// Convenience overload that resolves the <see cref="CachePolicy"/> from the configured
        /// <see cref="ICachePolicyProvider"/>.
        /// </summary>
        public Task<CachedHttpResponse<TPayload>> SendAsync<TPayload>(
            StreamingApiRequestBuilder builder,
            CacheKey key,
            CachingHttpHooks<TPayload>? hooks = null,
            CancellationToken cancellationToken = default)
        {
            if (_policyProvider is null)
            {
                throw new InvalidOperationException(
                    "CachingHttpExecutor was constructed without an ICachePolicyProvider. Pass an explicit CachePolicy to SendAsync.");
            }

            var policy = _policyProvider.GetPolicy(key?.Endpoint ?? string.Empty, key?.Parameters ?? new Dictionary<string, string>());
            return SendAsync(builder, key!, policy, hooks, cancellationToken);
        }

        /// <summary>
        /// Builds a request from <paramref name="builder"/> and sends it through the cache+resilience pipeline.
        /// </summary>
        /// <param name="builder">Request builder. Built once; for retries the executor buffers and clones the request.</param>
        /// <param name="key">Cache key (endpoint + parameters).</param>
        /// <param name="policy">Per-call cache policy. Determines TTL, soft-revalidate window, stale-if-error, etc.</param>
        /// <param name="hooks">Optional composition hooks (parse, request mutation, telemetry callbacks).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task<CachedHttpResponse<TPayload>> SendAsync<TPayload>(
            StreamingApiRequestBuilder builder,
            CacheKey key,
            CachePolicy policy,
            CachingHttpHooks<TPayload>? hooks = null,
            CancellationToken cancellationToken = default)
        {
            if (builder is null) throw new ArgumentNullException(nameof(builder));
            if (key is null) throw new ArgumentNullException(nameof(key));
            if (policy is null) throw new ArgumentNullException(nameof(policy));

            var endpoint = key.Endpoint;
            var parameters = key.Parameters;
            var cacheKeyString = _cache.GenerateCacheKey(endpoint, parameters);
            var shouldCache = policy.ShouldCache && _cache.ShouldCache(endpoint);
            var baseDuration = policy.Duration > TimeSpan.Zero ? policy.Duration : _cache.GetCacheDuration(endpoint);
            // Effective storage TTL must cover the soft-revalidate and stale-if-error windows so the executor
            // can retrieve the body for those paths even after the nominal cache duration has elapsed.
            // StoredAt is preserved on the cache entry so the windows are still bounded correctly.
            var cacheDuration = baseDuration;
            if (policy.SoftRevalidateWindow.HasValue && policy.SoftRevalidateWindow.Value > cacheDuration)
            {
                cacheDuration = policy.SoftRevalidateWindow.Value;
            }
            if (policy.StaleIfErrorTtl.HasValue && policy.StaleIfErrorTtl.Value > cacheDuration)
            {
                cacheDuration = policy.StaleIfErrorTtl.Value;
            }

            // ---- Soft-revalidate window: serve cached body without contacting the origin if young enough.
            if (shouldCache && policy.SoftRevalidateWindow.HasValue && policy.SoftRevalidateWindow.Value > TimeSpan.Zero)
            {
                var cachedSoft = _cache.Get<CachedHttpResponse>(endpoint, parameters);
                if (cachedSoft is not null && IsWithinWindow(cachedSoft.StoredAt, policy.SoftRevalidateWindow.Value))
                {
                    var soft = await BuildResultFromCachedAsync(cachedSoft, CacheHitKind.SoftRevalidate, hooks, cancellationToken).ConfigureAwait(false);
                    InvokeHit(hooks, CacheHitKind.SoftRevalidate, key);
                    return soft;
                }
            }

            // ---- Build request, attach conditional headers, allow caller mutation.
            using var request = builder.Build();
            await AttachConditionalHeadersAsync(request, cacheKeyString, endpoint, parameters, policy, shouldCache, cancellationToken).ConfigureAwait(false);
            try { hooks?.MutateRequest?.Invoke(request); }
            catch (Exception ex) { _logger.LogDebug(ex, "MutateRequest hook threw; ignoring"); }

            // ---- Send with resilience.
            using var rawResponse = await SendWithResilienceAsync(request, cancellationToken).ConfigureAwait(false);
            var statusCode = rawResponse.StatusCode;
            var statusInt = (int)statusCode;

            // ---- 304 Not Modified: fold into a synthetic 200 from cached body.
            if (statusCode == HttpStatusCode.NotModified)
            {
                var cachedHit = shouldCache ? _cache.Get<CachedHttpResponse>(endpoint, parameters) : null;
                if (cachedHit is not null)
                {
                    if (shouldCache)
                    {
                        // Refresh TTL by rewriting the same entry.
                        _cache.Set(endpoint, parameters, cachedHit, cacheDuration);
                    }
                    var fold = await BuildResultFromCachedAsync(cachedHit, CacheHitKind.NotModifiedFold, hooks, cancellationToken).ConfigureAwait(false);
                    InvokeHit(hooks, CacheHitKind.NotModifiedFold, key);
                    return fold;
                }
                // No cached body to fold into — surface the 304 to the caller as Passthrough.
                var passthrough304 = await BuildResultFromOriginAsync(rawResponse, CacheHitKind.Passthrough, hooks, cancellationToken).ConfigureAwait(false);
                InvokeHit(hooks, CacheHitKind.Passthrough, key);
                return passthrough304;
            }

            // ---- 2xx: buffer, cache, persist validators.
            if (statusInt >= 200 && statusInt < 300)
            {
                var bytes = await rawResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var contentType = rawResponse.Content.Headers.ContentType?.ToString();
                var etag = rawResponse.Headers.ETag?.Tag;
                var lastMod = rawResponse.Content.Headers.LastModified;
                var nowUtc = _timeProvider.GetUtcNow();

                if (shouldCache && IsResponseCacheable(rawResponse))
                {
                    var cacheEntry = new CachedHttpResponse
                    {
                        StatusCode = statusCode,
                        ContentType = contentType,
                        Body = bytes,
                        ETag = etag,
                        LastModified = lastMod,
                        StoredAt = nowUtc
                    };
                    _cache.Set(endpoint, parameters, cacheEntry, cacheDuration);
                }

                if (_conditionalState is not null && (!string.IsNullOrEmpty(etag) || lastMod.HasValue))
                {
                    await _conditionalState.SetValidatorsAsync(cacheKeyString, etag, lastMod, cancellationToken).ConfigureAwait(false);
                }

                var payload = await ParseAsync(rawResponse, bytes, contentType, hooks, cancellationToken).ConfigureAwait(false);
                InvokeHit(hooks, CacheHitKind.Miss, key);
                return new CachedHttpResponse<TPayload>
                {
                    StatusCode = statusCode,
                    Payload = payload,
                    Body = bytes,
                    ContentType = contentType,
                    ETag = etag,
                    LastModified = lastMod,
                    StoredAt = nowUtc,
                    HitKind = CacheHitKind.Miss
                };
            }

            // ---- 5xx: stale-if-error fallback.
            if (statusInt >= 500 && statusInt < 600 && shouldCache && policy.StaleIfErrorTtl.HasValue && policy.StaleIfErrorTtl.Value > TimeSpan.Zero)
            {
                var cachedStale = _cache.Get<CachedHttpResponse>(endpoint, parameters);
                if (cachedStale is not null && IsWithinWindow(cachedStale.StoredAt, policy.StaleIfErrorTtl.Value))
                {
                    var stale = await BuildResultFromCachedAsync(cachedStale, CacheHitKind.StaleIfError, hooks, cancellationToken).ConfigureAwait(false);
                    InvokeHit(hooks, CacheHitKind.StaleIfError, key);
                    return stale;
                }
            }

            // ---- 404/410: terminal — evict cached body + validators.
            if (policy.EvictOnTerminalStatus && (statusCode == HttpStatusCode.NotFound || statusCode == HttpStatusCode.Gone))
            {
                try
                {
                    if (_conditionalState is not null)
                    {
                        await _conditionalState.SetValidatorsAsync(cacheKeyString, eTag: null, lastModified: null, cancellationToken).ConfigureAwait(false);
                    }
                    _cache.ClearEndpoint(endpoint);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Cache eviction on terminal status {Status} failed for endpoint {Endpoint}", statusInt, endpoint);
                }

                try { hooks?.OnEvict?.Invoke(statusCode, key); }
                catch (Exception ex) { _logger.LogDebug(ex, "OnEvict hook threw; ignoring"); }

                var terminalResult = await BuildResultFromOriginAsync(rawResponse, CacheHitKind.EvictOnTerminal, hooks, cancellationToken).ConfigureAwait(false);
                InvokeHit(hooks, CacheHitKind.EvictOnTerminal, key);
                return terminalResult;
            }

            // ---- Other non-success (401/403/etc.): pass through.
            var passthrough = await BuildResultFromOriginAsync(rawResponse, CacheHitKind.Passthrough, hooks, cancellationToken).ConfigureAwait(false);
            InvokeHit(hooks, CacheHitKind.Passthrough, key);
            return passthrough;
        }

        // ---- helpers ----

        private bool IsWithinWindow(DateTimeOffset storedAt, TimeSpan window)
        {
            // Inclusive comparison; a 2-second tolerance guards against boundary flake under fast clocks.
            var now = _timeProvider.GetUtcNow();
            return (now - storedAt) <= (window + TimeSpan.FromSeconds(2));
        }

        private async Task AttachConditionalHeadersAsync(
            HttpRequestMessage request,
            string cacheKeyString,
            string endpoint,
            Dictionary<string, string> parameters,
            CachePolicy policy,
            bool shouldCache,
            CancellationToken cancellationToken)
        {
            if (_conditionalState is not null)
            {
                var validators = await _conditionalState.TryGetValidatorsAsync(cacheKeyString, cancellationToken).ConfigureAwait(false);
                if (validators is { } v)
                {
                    if (!string.IsNullOrEmpty(v.ETag))
                    {
                        request.Headers.TryAddWithoutValidation("If-None-Match", v.ETag);
                    }
                    if (v.LastModified.HasValue)
                    {
                        request.Headers.TryAddWithoutValidation("If-Modified-Since", v.LastModified.Value.ToString("R"));
                    }
                    return;
                }
            }

            // Fall back to in-cache validators when EnableConditionalRevalidation is set.
            if (!shouldCache || !policy.EnableConditionalRevalidation) return;

            var cached = _cache.Get<CachedHttpResponse>(endpoint, parameters);
            if (cached is null) return;

            if (!string.IsNullOrEmpty(cached.ETag))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
            }
            if (cached.LastModified.HasValue)
            {
                request.Headers.TryAddWithoutValidation("If-Modified-Since", cached.LastModified.Value.ToString("R"));
            }
        }

        private static bool IsResponseCacheable(HttpResponseMessage response)
        {
            var cc = response.Headers.CacheControl;
            if (cc is null) return true;
            return !(cc.NoStore || cc.Private);
        }

        private Task<HttpResponseMessage> SendWithResilienceAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Buffer the body once so retries can replay it without consuming the original content stream.
            // Static helpers in GenericResilienceExecutor handle the per-host gate, retry budget, and 429 backoff.
            // The resilience layer always uses real wall-clock time for retry delays — the executor's
            // _timeProvider drives cache windows (soft-revalidate, stale-if-error) which are conceptually
            // independent of retry backoff timing. This keeps unit tests with FakeTimeProvider deterministic
            // for windowing without freezing the resilience timer.
            return GenericResilienceExecutor.ExecuteWithResilienceAsync(
                request,
                sendAsync: (req, ct) => _invoker.SendAsync(req, ct),
                cloneRequestAsync: CloneRequestAsync,
                getHost: req => req.RequestUri?.Host,
                getStatusCode: resp => (int)resp.StatusCode,
                getRetryAfterDelay: GetRetryAfterDelay,
                policy: _resiliencePolicy,
                timeProvider: TimeProvider.System,
                cancellationToken: cancellationToken);
        }

        private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage source)
        {
            var clone = new HttpRequestMessage(source.Method, source.RequestUri)
            {
                Version = source.Version
            };
            foreach (var h in source.Headers)
            {
                clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            // Carry over options keys used by downstream layers (endpoint, profile, parameters, scope).
            foreach (var opt in (source.Options as IEnumerable<KeyValuePair<string, object?>>) ?? Enumerable.Empty<KeyValuePair<string, object?>>())
            {
                ((IDictionary<string, object?>)clone.Options)[opt.Key] = opt.Value;
            }

            if (source.Content is not null)
            {
                var bytes = await source.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                var content = new ByteArrayContent(bytes);
                foreach (var h in source.Content.Headers)
                {
                    content.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
                clone.Content = content;
            }
            return clone;
        }

        private static TimeSpan? GetRetryAfterDelay(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter is null) return null;
            if (retryAfter.Delta.HasValue) return retryAfter.Delta.Value;
            if (retryAfter.Date.HasValue)
            {
                var delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
            }
            return null;
        }

        private async Task<CachedHttpResponse<TPayload>> BuildResultFromCachedAsync<TPayload>(
            CachedHttpResponse cached,
            CacheHitKind kind,
            CachingHttpHooks<TPayload>? hooks,
            CancellationToken cancellationToken)
        {
            // Synthesize an HttpResponseMessage so the parse hook receives the same shape as a fresh response.
            using var synthetic = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(cached.Body ?? Array.Empty<byte>())
            };
            if (!string.IsNullOrEmpty(cached.ContentType))
            {
                synthetic.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(cached.ContentType);
            }
            if (cached.LastModified.HasValue)
            {
                synthetic.Content.Headers.LastModified = cached.LastModified;
            }
            if (!string.IsNullOrEmpty(cached.ETag) && EntityTagHeaderValue.TryParse(cached.ETag, out var et))
            {
                synthetic.Headers.ETag = et;
            }
            // Attach the standardized revalidated header so existing telemetry continues to work.
            var marker = kind == CacheHitKind.NotModifiedFold ? ArrCachingHeaders.RevalidatedValue
                       : kind == CacheHitKind.SoftRevalidate ? "soft"
                       : kind == CacheHitKind.StaleIfError ? "stale"
                       : "hit";
            synthetic.Headers.TryAddWithoutValidation(ArrCachingHeaders.RevalidatedHeader, marker);
            synthetic.Headers.TryAddWithoutValidation(ArrCachingHeaders.LegacyRevalidatedHeader, marker);
            if (kind == CacheHitKind.StaleIfError)
            {
                synthetic.Headers.TryAddWithoutValidation("Warning", "110 - Response is stale");
            }

            var payload = await ParseAsync(synthetic, cached.Body ?? Array.Empty<byte>(), cached.ContentType, hooks, cancellationToken).ConfigureAwait(false);

            return new CachedHttpResponse<TPayload>
            {
                StatusCode = HttpStatusCode.OK,
                Payload = payload,
                Body = cached.Body ?? Array.Empty<byte>(),
                ContentType = cached.ContentType,
                ETag = cached.ETag,
                LastModified = cached.LastModified,
                StoredAt = cached.StoredAt,
                HitKind = kind
            };
        }

        private async Task<CachedHttpResponse<TPayload>> BuildResultFromOriginAsync<TPayload>(
            HttpResponseMessage response,
            CacheHitKind kind,
            CachingHttpHooks<TPayload>? hooks,
            CancellationToken cancellationToken)
        {
            byte[] bytes;
            if (response.Content is null)
            {
                bytes = Array.Empty<byte>();
            }
            else
            {
                bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            var contentType = response.Content?.Headers.ContentType?.ToString();
            var etag = response.Headers.ETag?.Tag;
            var lastMod = response.Content?.Headers.LastModified;

            var payload = await ParseAsync(response, bytes, contentType, hooks, cancellationToken).ConfigureAwait(false);
            return new CachedHttpResponse<TPayload>
            {
                StatusCode = response.StatusCode,
                Payload = payload,
                Body = bytes,
                ContentType = contentType,
                ETag = etag,
                LastModified = lastMod,
                StoredAt = _timeProvider.GetUtcNow(),
                HitKind = kind
            };
        }

        private async Task<TPayload?> ParseAsync<TPayload>(
            HttpResponseMessage response,
            byte[] bytes,
            string? contentType,
            CachingHttpHooks<TPayload>? hooks,
            CancellationToken cancellationToken)
        {
            if (hooks?.ParseAsync is null)
            {
                return default;
            }
            try
            {
                // Replace content with a fresh ByteArrayContent so the parse hook can read the body even if the
                // origin's content stream was already consumed by the executor.
                using var clone = new HttpResponseMessage(response.StatusCode)
                {
                    Content = new ByteArrayContent(bytes)
                };
                if (!string.IsNullOrEmpty(contentType))
                {
                    clone.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                }
                foreach (var h in response.Headers)
                {
                    clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
                return await hooks.ParseAsync(clone, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ParseAsync hook failed; returning default payload");
                return default;
            }
        }

        private void InvokeHit<TPayload>(CachingHttpHooks<TPayload>? hooks, CacheHitKind kind, CacheKey key)
        {
            try { hooks?.OnHit?.Invoke(kind, key); }
            catch (Exception ex) { _logger.LogDebug(ex, "OnHit hook threw; ignoring"); }
        }
    }
}
