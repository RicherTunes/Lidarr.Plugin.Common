using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Delegating handler that injects a bearer token provided by an <see cref="IStreamingTokenProvider"/>.
    /// Useful for services that manage their own token lifecycle outside of the OAuth handler.
    /// </summary>
    public sealed class TokenDelegatingHandler : DelegatingHandler
    {
        private readonly IStreamingTokenProvider _tokenProvider;

        public TokenDelegatingHandler(IStreamingTokenProvider tokenProvider)
        {
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var token = await _tokenProvider.GetAccessTokenAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
