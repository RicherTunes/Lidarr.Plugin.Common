using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// DelegatingHandler that observes Retry-After headers and notifies an observer.
    /// </summary>
    public sealed class RateLimitTelemetryHandler : DelegatingHandler
    {
        private readonly IRateLimitObserver _observer;
#if NET8_0_OR_GREATER
        private readonly TimeProvider _timeProvider;
        public RateLimitTelemetryHandler(IRateLimitObserver observer, TimeProvider timeProvider, HttpMessageHandler inner)
            : base(inner)
        {
            _observer = observer ?? throw new ArgumentNullException(nameof(observer));
            _timeProvider = timeProvider ?? TimeProvider.System;
        }
#else
        public RateLimitTelemetryHandler(IRateLimitObserver observer, HttpMessageHandler inner)
            : base(inner)
        {
            _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        }
#endif

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var res = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)res.StatusCode == 429 || res.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                var ra = res.Headers.RetryAfter;
                if (ra is not null)
                {
#if NET8_0_OR_GREATER
                    var now = _timeProvider.GetUtcNow();
#else
                    var now = DateTimeOffset.UtcNow;
#endif
                    var delay = ra.Delta ?? (ra.Date.HasValue ? ra.Date.Value - now : TimeSpan.Zero);
                    if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                    _observer.RecordRetryAfter(delay, now);
                }
            }
            return res;
        }
    }
}
