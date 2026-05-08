using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Optional composition hooks for <see cref="CachingHttpExecutor"/>. All callbacks are best-effort and
    /// must not throw; the executor logs and swallows exceptions raised by hook callbacks.
    /// </summary>
    /// <typeparam name="TPayload">The parsed payload type produced by the parse hook.</typeparam>
    /// <param name="ParseAsync">
    /// Parses the buffered response body into a <typeparamref name="TPayload"/>. Called for every code path that
    /// produces a payload (fresh origin response, 304 fold, soft-revalidate, stale-if-error). When omitted,
    /// <see cref="CachedHttpResponse{TPayload}.Payload"/> is left at its default value.
    /// </param>
    /// <param name="MutateRequest">
    /// Last-mile mutation of the outgoing <see cref="HttpRequestMessage"/> after authentication and conditional
    /// headers have been attached. Use to add per-request decorations (e.g., correlation IDs).
    /// </param>
    /// <param name="OnEvict">
    /// Invoked when a terminal status (e.g., 404, 410) causes the cache entry and conditional validators to be
    /// evicted. The <see cref="HttpStatusCode"/> is the offending response code.
    /// </param>
    /// <param name="OnHit">
    /// Invoked when a cache outcome is decided (after the executor finishes deciding hit/miss/fold/etc.). Useful
    /// for incrementing per-endpoint metrics counters.
    /// </param>
    public sealed record CachingHttpHooks<TPayload>(
        Func<HttpResponseMessage, CancellationToken, Task<TPayload>>? ParseAsync = null,
        Action<HttpRequestMessage>? MutateRequest = null,
        Action<HttpStatusCode, CacheKey>? OnEvict = null,
        Action<CacheHitKind, CacheKey>? OnHit = null)
    {
        /// <summary>
        /// When <see langword="true"/>, exceptions raised by <see cref="ParseAsync"/> are surfaced to the
        /// caller rather than being absorbed into a default payload. Default: <see langword="false"/>
        /// (preserves the previous swallow-and-log behavior).
        /// </summary>
        /// <remarks>
        /// Source: qobuzarr Phase 3b adoption feedback ("legacy callers depend on <c>JsonReaderException</c>
        /// propagating from <c>JsonConvert.DeserializeObject</c>"). Plugins adopting strict parsing should set
        /// this to <c>true</c>; plugins that prefer the executor's resilience-first behavior should leave it
        /// at the default.
        /// </remarks>
        public bool PropagateParseExceptions { get; init; }
    }
}
