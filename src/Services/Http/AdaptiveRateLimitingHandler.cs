using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Performance;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// HttpClient delegating handler that gates every egress request through
    /// <see cref="IUniversalAdaptiveRateLimiter"/>.
    ///
    /// <para>
    /// Pre-send: calls <see cref="IUniversalAdaptiveRateLimiter.WaitIfNeededAsync"/> to
    /// honour token-bucket and active-429-cooldown state. <see cref="ObjectDisposedException"/>
    /// is swallowed — a disposal race must not block the entire pipeline.
    /// </para>
    ///
    /// <para>
    /// Post-send: feeds the response to <see cref="IUniversalAdaptiveRateLimiter.RecordResponse"/>
    /// so the limiter can tighten or relax its adaptive budget.
    /// </para>
    ///
    /// <para>
    /// 429 + <c>Retry-After</c>: the header is honoured with a Task.Delay before returning
    /// to the caller, so any upstream retry policy (Polly, etc.) waits the right amount of
    /// time instead of immediately burning through the remaining budget.
    /// </para>
    ///
    /// <para>
    /// The endpoint key passed to the limiter is derived from the request URI's host and
    /// first path segment, giving each logical endpoint its own independent budget while
    /// keeping total cardinality bounded.
    /// </para>
    /// </summary>
    public class AdaptiveRateLimitingHandler : DelegatingHandler
    {
        private readonly IUniversalAdaptiveRateLimiter _limiter;
        private readonly string _serviceName;
        private readonly ILogger? _logger;

        /// <summary>
        /// Initialises the handler.
        /// </summary>
        /// <param name="limiter">The shared rate limiter instance.</param>
        /// <param name="serviceName">
        ///   Human-readable service name used as the first key segment in the limiter
        ///   (e.g. "Tidal", "Qobuz"). Must not be null or whitespace.
        /// </param>
        /// <param name="logger">Optional logger; when null, 429 warnings are silenced.</param>
        public AdaptiveRateLimitingHandler(
            IUniversalAdaptiveRateLimiter limiter,
            string serviceName,
            ILogger? logger = null)
        {
            _limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
            if (string.IsNullOrWhiteSpace(serviceName))
                throw new ArgumentException("Service name must not be null or whitespace.", nameof(serviceName));
            _serviceName = serviceName;
            _logger = logger;
        }

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string endpointKey = BuildEndpointKey(request);

            // Pre-send: wait for the limiter to permit this request.
            try
            {
                await _limiter.WaitIfNeededAsync(_serviceName, endpointKey, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Limiter shut down (plugin disposing). Continue without gating.
            }

            HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // Post-send: feed the response back to the limiter for adaptive adjustment.
            try
            {
                _limiter.RecordResponse(_serviceName, endpointKey, response);
            }
            catch (ObjectDisposedException) { /* limiter shut down mid-request */ }

            // Honour Retry-After on 429 so the caller's retry policy waits correctly.
            if (response.StatusCode == HttpStatusCode.TooManyRequests
                && response.Headers.RetryAfter is { } retryAfter)
            {
                TimeSpan delay = ResolveRetryAfter(retryAfter);
                if (delay > TimeSpan.Zero)
                {
                    _logger?.LogWarning(
                        "{Service} returned 429 for {Endpoint}; honoring Retry-After of {Delay}",
                        _serviceName, endpointKey, delay);
                    try
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Caller cancelled; fall through and return the 429 response as-is.
                    }
                }
            }

            return response;
        }

        /// <summary>
        /// Derives a stable endpoint key from the request URI.
        /// Uses <c>host:firstPathSegment</c> to balance specificity vs. cardinality:
        /// <list type="bullet">
        /// <item><description><c>api.tidal.com/v1/search</c> → <c>api.tidal.com:v1</c></description></item>
        /// <item><description><c>sp-ap-eu.audio.tidal.com/.../seg.mp4</c> → <c>sp-ap-eu.audio.tidal.com:</c></description></item>
        /// </list>
        /// </summary>
        private static string BuildEndpointKey(HttpRequestMessage request)
        {
            Uri? uri = request.RequestUri;
            if (uri is null) return "unknown";
            string host = uri.Host;
            string firstSeg = uri.Segments.Length > 1 ? uri.Segments[1].TrimEnd('/') : string.Empty;
            return $"{host}:{firstSeg}";
        }

        /// <summary>
        /// Resolves the effective delay from a <c>Retry-After</c> header value.
        /// Precedence: Delta → Date → Zero.
        /// </summary>
        private static TimeSpan ResolveRetryAfter(System.Net.Http.Headers.RetryConditionHeaderValue retryAfter)
        {
            if (retryAfter.Delta is { } delta) return delta;
            if (retryAfter.Date is { } date)
            {
                TimeSpan untilDate = date - DateTimeOffset.UtcNow;
                return untilDate > TimeSpan.Zero ? untilDate : TimeSpan.Zero;
            }
            return TimeSpan.Zero;
        }
    }
}
