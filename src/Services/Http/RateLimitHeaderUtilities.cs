using System;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Utilities for parsing HTTP rate-limit-related headers, and for deriving
    /// stable endpoint keys used by <see cref="Performance.IUniversalAdaptiveRateLimiter"/>.
    ///
    /// Centralises logic that was previously duplicated across plugin-specific
    /// DelegatingHandlers (tidalarr's <c>TidalRateLimitingHandler</c> and qobuzarr's
    /// <c>QobuzRateLimitingHandler</c>).
    /// </summary>
    public static class RateLimitHeaderUtilities
    {
        /// <summary>
        /// Resolve a <see cref="RetryConditionHeaderValue"/> (the <c>Retry-After</c> header)
        /// to a positive <see cref="TimeSpan"/> delay, or <see cref="TimeSpan.Zero"/> when
        /// the header is absent or describes a time already in the past.
        ///
        /// Behaviour mirrors what tidalarr and qobuzarr both implemented inline:
        /// - Prefer the delta-seconds form if present.
        /// - Otherwise interpret the HTTP-date form as "wait until that absolute UTC time".
        /// - Past dates and missing values both return <see cref="TimeSpan.Zero"/> so callers
        ///   can treat "no wait" uniformly.
        /// </summary>
        /// <param name="retryAfter">The header value parsed by <see cref="HttpResponseMessage.Headers"/>.</param>
        /// <returns>A non-negative delay; <see cref="TimeSpan.Zero"/> when no wait is implied.</returns>
        public static TimeSpan ResolveRetryAfter(RetryConditionHeaderValue? retryAfter)
        {
            if (retryAfter is null) return TimeSpan.Zero;
            if (retryAfter.Delta is { } delta) return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
            if (retryAfter.Date is { } date)
            {
                TimeSpan untilDate = date - DateTimeOffset.UtcNow;
                return untilDate > TimeSpan.Zero ? untilDate : TimeSpan.Zero;
            }
            return TimeSpan.Zero;
        }

        /// <summary>
        /// Convenience overload that pulls the <c>Retry-After</c> header off a response
        /// and resolves it in one step. Returns <see cref="TimeSpan.Zero"/> when the
        /// response is null or carries no <c>Retry-After</c> header.
        /// </summary>
        public static TimeSpan ResolveRetryAfter(HttpResponseMessage? response)
        {
            if (response is null) return TimeSpan.Zero;
            return ResolveRetryAfter(response.Headers.RetryAfter);
        }

        /// <summary>
        /// Build a rate-limiter endpoint key from a request URI using host + first path
        /// segment. This balances specificity (distinct API surfaces get independent
        /// budgets) against cardinality (a per-item URL doesn't produce a unique bucket
        /// per request).
        ///
        /// Examples:
        /// <list type="bullet">
        ///   <item><c>https://api.tidal.com/v1/search?q=foo</c> → <c>api.tidal.com:v1</c></item>
        ///   <item><c>https://sp-ap-eu.audio.tidal.com/.../seg.mp4</c> → <c>sp-ap-eu.audio.tidal.com:</c></item>
        ///   <item><c>https://www.qobuz.com/api.json/0.2/album/get</c> → <c>www.qobuz.com:api.json</c></item>
        /// </list>
        /// </summary>
        /// <param name="request">The outgoing HTTP request.</param>
        /// <param name="unknownKey">Key to return when the request URI is null.</param>
        /// <returns>A stable per-endpoint key suitable for keying a rate limiter.</returns>
        public static string BuildHostFirstSegmentKey(HttpRequestMessage request, string unknownKey = "unknown")
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            return BuildHostFirstSegmentKey(request.RequestUri, unknownKey);
        }

        /// <summary>
        /// Build a rate-limiter endpoint key from a URI using host + first path segment.
        /// See <see cref="BuildHostFirstSegmentKey(HttpRequestMessage, string)"/> for examples.
        /// </summary>
        public static string BuildHostFirstSegmentKey(Uri? uri, string unknownKey = "unknown")
        {
            if (uri is null) return unknownKey;
            string host = uri.Host;
            string firstSeg = uri.Segments.Length > 1 ? uri.Segments[1].TrimEnd('/') : string.Empty;
            return host + ":" + firstSeg;
        }
    }
}
