using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Caching;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Opt-in helper that composes the existing resilience pipeline with simple GET caching and optional 304 revalidation.
    /// This method is conservative and only caches successful GET responses.
    /// </summary>
    internal static class HttpClientResilienceExtensions
    {
        public static async Task<HttpResponseMessage> ExecuteWithResilienceAndCachingAsync(
            this HttpClient httpClient,
            HttpRequestMessage request,
            IResiliencePolicyProvider resilience,
            IStreamingResponseCache cache,
            Lidarr.Plugin.Common.Services.Deduplication.RequestDeduplicator deduplicator,
            IConditionalRequestState? conditionalState = null,
            CancellationToken cancellationToken = default)
        {
            if (deduplicator == null) throw new ArgumentNullException(nameof(deduplicator));
            if (request?.Method == HttpMethod.Get)
            {
                var key = HttpClientExtensions.BuildRequestDedupKey(request);
                return await deduplicator.GetOrCreateAsync(key, () =>
                    httpClient.ExecuteWithResilienceAndCachingAsync(request, resilience, cache, conditionalState, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }

            return await httpClient.ExecuteWithResilienceAndCachingAsync(request, resilience, cache, conditionalState, cancellationToken).ConfigureAwait(false);
        }
        public static async Task<HttpResponseMessage> ExecuteWithResilienceAndCachingAsync(
            this HttpClient httpClient,
            HttpRequestMessage request,
            IResiliencePolicyProvider resilience,
            IStreamingResponseCache cache,
            IConditionalRequestState? conditionalState = null,
            CancellationToken cancellationToken = default)
        {
            if (httpClient is null) throw new ArgumentNullException(nameof(httpClient));
            if (request is null) throw new ArgumentNullException(nameof(request));
            if (resilience is null) throw new ArgumentNullException(nameof(resilience));
            if (cache is null) throw new ArgumentNullException(nameof(cache));

            var isGet = request.Method == HttpMethod.Get;
            if (!isGet)
            {
                var s = resilience.Get("default");
                return await httpClient.ExecuteWithResilienceAsync(
                    request,
                    s.MaxRetries,
                    s.RetryBudget,
                    s.MaxConcurrencyPerHost,
                    s.PerRequestTimeout,
                    cancellationToken).ConfigureAwait(false);
            }

            // Derive endpoint + parameters for cache key
            var (endpoint, parameters) = DeriveEndpointAndParams(request);
            var cacheKey = cache.GenerateCacheKey(endpoint, parameters);

            // Attach conditional headers when we have validators
            if (conditionalState != null)
            {
                var validators = await conditionalState.TryGetValidatorsAsync(cacheKey, cancellationToken).ConfigureAwait(false);
                if (validators is { } v)
                {
                    if (!string.IsNullOrEmpty(v.ETag)) request.Headers.TryAddWithoutValidation("If-None-Match", v.ETag);
                    if (v.LastModified.HasValue) request.Headers.TryAddWithoutValidation("If-Modified-Since", v.LastModified.Value.ToString("R"));
                }
            }
            else
            {
                try
                {
                    // If policy enables conditional revalidation and we have a cached entry, attach validators from cache.
                    if (cache is StreamingResponseCache impl && impl.IsConditionalRevalidationEnabled(endpoint, parameters))
                    {
                        var cachedForValidation = cache.Get<CachedHttpResponse>(endpoint, parameters);
                        if (cachedForValidation != null)
                        {
                            if (!string.IsNullOrEmpty(cachedForValidation.ETag))
                            {
                                request.Headers.TryAddWithoutValidation("If-None-Match", cachedForValidation.ETag);
                            }
                            if (cachedForValidation.LastModified.HasValue)
                            {
                                request.Headers.TryAddWithoutValidation("If-Modified-Since", cachedForValidation.LastModified.Value.ToString("R"));
                            }
                        }
                    }
                }
                catch { /* best-effort; ignore validator attach errors */ }
            }

            // Try cached body for potential 304 path
            var shouldCache = cache.ShouldCache(endpoint);
            var cacheDuration = cache.GetCacheDuration(endpoint);
            var cached = shouldCache ? cache.Get<CachedHttpResponse>(endpoint, parameters) : null;

            var profile = request.Options.TryGetValue(new HttpRequestOptionsKey<string>("arr.plugin.http.profile"), out var prof) ? prof : "default";
            var r = resilience.Get(profile);
            var totalCap = r.MaxTotalConcurrencyPerHost > 0 ? r.MaxTotalConcurrencyPerHost : r.MaxConcurrencyPerHost;
            using var response = await httpClient.ExecuteWithResilienceAsyncInternal(
                request,
                r.MaxRetries,
                r.RetryBudget,
                r.MaxConcurrencyPerHost,
                totalCap,
                r.PerRequestTimeout,
                cancellationToken).ConfigureAwait(false);
            // Note: the aggregate per-host cap is applied inside ExecuteWithResilienceAsyncCore when called via policy overload.

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                var cachedHit = cached ?? (shouldCache ? cache.Get<CachedHttpResponse>(endpoint, parameters) : null);
                if (cachedHit is null)
                {
                    return response; // nothing cached; surface 304
                }
                // synthetic 200 from cache
                var ok = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(cachedHit.Body)
                };
                if (!string.IsNullOrEmpty(cachedHit.ContentType))
                {
                    ok.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(cachedHit.ContentType);
                }
                if (!string.IsNullOrEmpty(cachedHit.ETag) && EntityTagHeaderValue.TryParse(cachedHit.ETag, out var et))
                {
                    ok.Headers.ETag = et;
                }
                if (cachedHit.LastModified.HasValue)
                {
                    ok.Content.Headers.LastModified = cachedHit.LastModified;
                }
                // Prefer standardized header; include legacy for transition
                ok.Headers.TryAddWithoutValidation(Lidarr.Plugin.Common.Services.Caching.ArrCachingHeaders.RevalidatedHeader, Lidarr.Plugin.Common.Services.Caching.ArrCachingHeaders.RevalidatedValue);
                ok.Headers.TryAddWithoutValidation(Lidarr.Plugin.Common.Services.Caching.ArrCachingHeaders.LegacyRevalidatedHeader, Lidarr.Plugin.Common.Services.Caching.ArrCachingHeaders.RevalidatedValue);
                // Refresh TTL without rewriting payload
                if (shouldCache)
                {
                    cache.Set(endpoint, parameters, cachedHit, cacheDuration);
                }
                try { Observability.Metrics.CacheRevalidate.Add(1, new KeyValuePair<string, object?>("endpoint", endpoint)); } catch { }
                return ok;
            }

            if (!response.IsSuccessStatusCode)
            {
                return response;
            }

            // Cache successful GET (respect private/no-store)
            var cc = response.Headers.CacheControl;
            var canCache = shouldCache && !(cc?.NoStore ?? false) && !(cc?.Private ?? false);

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.ToString();
            var etag = response.Headers.ETag?.Tag;
            var lastMod = response.Content.Headers.LastModified;

            if (canCache)
            {
                cache.Set(endpoint, parameters, new CachedHttpResponse
                {
                    StatusCode = response.StatusCode,
                    ContentType = contentType,
                    Body = bytes,
                    ETag = etag,
                    LastModified = lastMod,
                    StoredAt = DateTimeOffset.UtcNow
                }, cacheDuration);
            }

            if (conditionalState != null && (!string.IsNullOrEmpty(etag) || lastMod.HasValue))
            {
                await conditionalState.SetValidatorsAsync(cacheKey, etag, lastMod, cancellationToken).ConfigureAwait(false);
            }

            // Replace content with buffered bytes for the caller
            var passthrough = new HttpResponseMessage(response.StatusCode);
            foreach (var h in response.Headers)
            {
                passthrough.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            passthrough.Content = new ByteArrayContent(bytes);
            if (!string.IsNullOrEmpty(contentType))
            {
                passthrough.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }
            if (lastMod.HasValue)
            {
                passthrough.Content.Headers.LastModified = lastMod;
            }
            return passthrough;
        }

        private static (string Endpoint, Dictionary<string, string> Parameters) DeriveEndpointAndParams(HttpRequestMessage request)
        {
            // Prefer options
            if (request.Options.TryGetValue(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.EndpointKey, out var ep) && request.Options.TryGetValue(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.ParametersKey, out var qp))
            {
                // Include the canonical string directly to ensure cache key invariants match dedup
                var dict = ParseQuery(qp);
                dict[Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.ParametersKey.Key] = qp ?? string.Empty;
                if (request.Options.TryGetValue(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.AuthScopeKey, out string? scope) && !string.IsNullOrWhiteSpace(scope))
                {
                    dict["scope"] = scope;
                }
                return (ep ?? NormalizePath(request.RequestUri), dict);
            }
            if (request.Options.TryGetValue(new HttpRequestOptionsKey<string>("arr.plugin.http.endpoint"), out ep) && request.Options.TryGetValue(new HttpRequestOptionsKey<string>("arr.plugin.http.params"), out qp))
            {
                var dict = ParseQuery(qp);
                dict[Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.ParametersKey.Key] = qp ?? string.Empty;
                if (request.Options.TryGetValue(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.AuthScopeKey, out string? scope) && !string.IsNullOrWhiteSpace(scope))
                {
                    dict["scope"] = scope;
                }
                return (ep ?? NormalizePath(request.RequestUri), dict);
            }
            return (NormalizePath(request.RequestUri), ParseQuery(request.RequestUri?.Query ?? string.Empty));
        }

        private static string NormalizePath(Uri? uri)
        {
            if (uri is null) return "/";
            var path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('?')[0];
            return string.IsNullOrWhiteSpace(path) ? "/" : path;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(query)) return dict;
            var q = query.StartsWith("?", StringComparison.Ordinal) ? query[1..] : query;
            foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kv = part.Split('=', 2);
                var k = Uri.UnescapeDataString(kv[0]);
                var v = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
                // For cache key, collapse multivalue by joining with comma when repeated keys appear
                if (dict.TryGetValue(k, out var existing))
                {
                    // Keep values sorted at the end (StreamingResponseCache canonicalization prefers the canonical string when present)
                    dict[k] = existing + "," + v;
                }
                else
                {
                    dict[k] = v;
                }
            }
            return dict;
        }
    }
}
