using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// A transport-agnostic resilience executor that applies a unified retry policy
    /// (429/Retry-After aware, exponential backoff with jitter, retry budget, and per-host concurrency gates)
    /// over arbitrary HTTP-like clients.
    ///
    /// Consumers supply small adapter lambdas to integrate different client stacks
    /// (e.g., System.Net.Http or NzbDrone.Common.Http) without introducing new dependencies here.
    /// </summary>
    public static class GenericResilienceExecutor
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _hostGates = new();

        private static SemaphoreSlim GetHostGate(string? host, int maxConcurrencyPerHost)
        {
            host ??= "__unknown__";
            return _hostGates.GetOrAdd(host, _ => new SemaphoreSlim(maxConcurrencyPerHost, maxConcurrencyPerHost));
        }

        /// <summary>
        /// Execute a request with resilience in a transport-agnostic way.
        /// </summary>
        public static async Task<TResponse> ExecuteWithResilienceAsync<TRequest, TResponse>(
            TRequest request,
            Func<TRequest, CancellationToken, Task<TResponse>> sendAsync,
            Func<TRequest, Task<TRequest>> cloneRequestAsync,
            Func<TRequest, string?> getHost,
            Func<TResponse, int> getStatusCode,
            Func<TResponse, TimeSpan?> getRetryAfterDelay,
            int maxRetries = 5,
            TimeSpan? retryBudget = null,
            int maxConcurrencyPerHost = 6,
            CancellationToken cancellationToken = default)
        {
            retryBudget ??= TimeSpan.FromSeconds(60);
            var deadline = DateTime.UtcNow + retryBudget.Value;
            var attempt = 0;

            var host = getHost(request);
            var gate = GetHostGate(host, Math.Max(1, maxConcurrencyPerHost));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                while (true)
                {
                    attempt++;

                    var attemptRequest = await cloneRequestAsync(request).ConfigureAwait(false);
                    var response = await sendAsync(attemptRequest, cancellationToken).ConfigureAwait(false);

                    var status = getStatusCode(response);
                    var retryable = status == (int)HttpStatusCode.RequestTimeout // 408
                                   || status == (int)HttpStatusCode.TooManyRequests // 429
                                   || (status >= 500 && status <= 599);

                    if (!retryable || attempt >= maxRetries)
                    {
                        return response;
                    }

                    var delay = getRetryAfterDelay(response) ??
                                TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))) + GetJitter();

                    var now = DateTime.UtcNow;
                    if (now + delay > deadline)
                    {
                        // Budget exceeded; return last response
                        return response;
                    }

                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        private static TimeSpan GetJitter()
        {
            var ms = Random.Shared.Next(50, 250);
            return TimeSpan.FromMilliseconds(ms);
        }
    }
}

