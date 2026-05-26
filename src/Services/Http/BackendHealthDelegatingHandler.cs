using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Delegating handler that gates outbound HTTP requests through
    /// <see cref="BackendHealthCache"/>.
    ///
    /// <para>
    /// When the target host suffered a connection-class failure (SocketException / DNS)
    /// within the last <see cref="BackendHealthCache.DefaultGraceSeconds"/> seconds, the
    /// call short-circuits immediately with an <see cref="HttpRequestException"/> rather
    /// than burning the full retry budget.
    /// </para>
    ///
    /// <para>
    /// On any successful HTTP exchange the down-state is cleared so future calls go through.
    /// </para>
    ///
    /// <para>
    /// The <c>classifyProvider</c> hook controls how the request's host name is
    /// mapped to a stable provider key stored in the cache. Two built-in patterns are
    /// supported:
    /// <list type="bullet">
    /// <item>
    ///   <description>
    ///     <b>Subdomain-mapping</b> (e.g. Tidal): supply a <c>Func&lt;string,string&gt;</c>
    ///     that maps each host to a logical group. Multiple hosts map to one or more
    ///     provider keys depending on the topology.
    ///   </description>
    /// </item>
    /// <item>
    ///   <description>
    ///     <b>Fixed provider</b> (e.g. Apple Music): use the
    ///     <see cref="BackendHealthDelegatingHandler(BackendHealthCache, string, ILogger?)"/>
    ///     convenience overload; all hosts map to the same fixed provider key and the base
    ///     URL (scheme + host) serves as the fine-grained cache slot.
    ///   </description>
    /// </item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// This gate is independent of <c>AuthFailureGate</c>. Auth-gate trips on repeated
    /// 401/403 (auth-failure cascade); this gate trips on repeated connection-refused / DNS
    /// failures (network-down cascade). Both can coexist.
    /// </para>
    /// </summary>
    public class BackendHealthDelegatingHandler : DelegatingHandler
    {
        private readonly BackendHealthCache _cache;
        private readonly Func<string, string> _classifyProvider;
        private readonly ILogger _logger;
        private readonly string? _serviceName;

        /// <summary>
        /// Initialises the handler with a custom host→provider mapping function.
        /// </summary>
        /// <param name="cache">The shared health cache.</param>
        /// <param name="classifyProvider">
        ///   Maps a request host string to a stable provider key. When <see langword="null"/>
        ///   the host itself is used as the provider key.
        /// </param>
        /// <param name="logger">Optional logger.</param>
        public BackendHealthDelegatingHandler(
            BackendHealthCache cache,
            Func<string, string>? classifyProvider = null,
            ILogger? logger = null)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _classifyProvider = classifyProvider ?? (host => host);
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>
        /// Convenience overload for plugins where all requests map to one fixed provider key
        /// (e.g. "apple:music"), while each host's base URL maintains an independent cache slot.
        /// </summary>
        /// <param name="cache">The shared health cache.</param>
        /// <param name="fixedProvider">
        ///   Provider key applied to every request regardless of host (e.g. "apple:music").
        ///   Must not be null or whitespace.
        /// </param>
        /// <param name="logger">Optional logger.</param>
        public BackendHealthDelegatingHandler(
            BackendHealthCache cache,
            string fixedProvider,
            ILogger? logger = null)
        {
            if (string.IsNullOrWhiteSpace(fixedProvider))
                throw new ArgumentException("Fixed provider must not be null or whitespace.", nameof(fixedProvider));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _classifyProvider = _ => fixedProvider;
            _serviceName = fixedProvider;
            _logger = logger ?? NullLogger.Instance;
        }

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string host = request.RequestUri?.Host ?? string.Empty;
            string provider = _classifyProvider(host);
            string baseUrl = ExtractBaseUrl(request.RequestUri);

            // Fast-fail: check cache BEFORE sending the request.
            if (_cache.IsKnownDown(provider, baseUrl, out string? downReason))
            {
                string label = _serviceName ?? provider;
                _logger.LogDebug(
                    "[BackendHealthCache] Skipping {Label} request to {BaseUrl} — {DownReason}",
                    label, baseUrl, downReason);
                throw new HttpRequestException(
                    $"{label} backend known-down: {downReason}",
                    inner: null,
                    statusCode: null);
            }

            try
            {
                HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

                // On any successful HTTP exchange clear the down-state.
                if (response.IsSuccessStatusCode)
                {
                    _cache.MarkUp(provider, baseUrl);
                }

                return response;
            }
            catch (HttpRequestException ex) when (BackendHealthCache.IsConnectionClassFailure(ex))
            {
                _cache.MarkDown(provider, baseUrl, ex);
                throw;
            }
            catch (OperationCanceledException)
            {
                // Timeout / cancellation — not a connection-class failure; don't mark down.
                throw;
            }
        }

        /// <summary>
        /// Returns the host-only base URL (scheme + host) so all paths on the same host
        /// share one cache slot.
        /// </summary>
        private static string ExtractBaseUrl(Uri? uri)
        {
            if (uri is null) return string.Empty;
            return uri.IsAbsoluteUri
                ? $"{uri.Scheme}://{uri.Host}"
                : uri.Host;
        }
    }
}
