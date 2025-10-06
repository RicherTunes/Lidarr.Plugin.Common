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
    public static class HttpClientResilienceExtensions
    {
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

            // Try cached body for potential 304 path
            var shouldCache = cache.ShouldCache(endpoint);
            var cacheDuration = cache.GetCacheDuration(endpoint);
            var cached = shouldCache ? cache.Get<CachedHttpResponse>(endpoint, parameters) : null;

            var profile = request.Options.TryGetValue(new HttpRequestOptionsKey<string>("arr.plugin.http.profile"), out var prof) ? prof : "default";
            var r = resilience.Get(profile);
            using var response = await httpClient.ExecuteWithResilienceAsync(
                request,
                r.MaxRetries,
                r.RetryBudget,
                r.MaxConcurrencyPerHost,
                r.PerRequestTimeout,
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified && cached is { } cachedHit)
            {
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
                return (ep ?? NormalizePath(request.RequestUri), ParseQuery(qp));
            }
            if (request.Options.TryGetValue(new HttpRequestOptionsKey<string>("arr.plugin.http.endpoint"), out ep) && request.Options.TryGetValue(new HttpRequestOptionsKey<string>("arr.plugin.http.params"), out qp))
            {
                return (ep ?? NormalizePath(request.RequestUri), ParseQuery(qp));
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
                // For cache key, collapse multivalue to last occurrence (callers should canonicalize via Options when needed)
                dict[k] = v;
            }
            return dict;
        }
    }
}
