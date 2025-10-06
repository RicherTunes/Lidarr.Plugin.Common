using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Utilities;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Delegating handler that injects Bearer tokens and performs a single-flight token refresh on 401.
    /// Relies on an IStreamingTokenProvider implementation.
    /// </summary>
    public class OAuthDelegatingHandler : DelegatingHandler
    {
        private readonly IStreamingTokenProvider _tokenProvider;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);

        public OAuthDelegatingHandler(IStreamingTokenProvider tokenProvider, ILogger logger)
        {
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Inject current token
            var token = await _tokenProvider.GetAccessTokenAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.Unauthorized || !_tokenProvider.SupportsRefresh)
            {
                return response;
            }

            // 401: try single-flight refresh and retry once
            response.Dispose();

            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var currentToken = await _tokenProvider.GetAccessTokenAsync().ConfigureAwait(false);
                if (!string.IsNullOrEmpty(currentToken) && currentToken != token)
                {
                    _logger.LogDebug("[{Service}] Token already refreshed by another request; reusing cached token", _tokenProvider.ServiceName);
                }
                else
                {
                    var refreshed = await _tokenProvider.RefreshTokenAsync().ConfigureAwait(false);
                    if (string.IsNullOrEmpty(refreshed))
                    {
                        _logger.LogWarning("[{Service}] Token refresh returned empty token after 401", _tokenProvider.ServiceName);
                        return new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request };
                    }

                    if (refreshed == token)
                    {
                        _logger.LogDebug("[{Service}] Token unchanged after refresh; proceeding to retry once", _tokenProvider.ServiceName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Service}] Token refresh failed after 401", _tokenProvider.ServiceName);
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request };
            }
            finally
            {
                _refreshLock.Release();
            }

            // Retry once with a fresh token (buffer content safely)
            using var retry = await HttpClientExtensions.CloneForRetryAsync(request).ConfigureAwait(false);
            var fresh = await _tokenProvider.GetAccessTokenAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(fresh))
            {
                retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
            }

            var retryResponse = await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
            return retryResponse;
        }
    }
}

