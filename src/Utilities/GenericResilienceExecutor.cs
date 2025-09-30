using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// A transport-agnostic resilience executor that applies a unified retry policy
    /// (429/Retry-After aware, exponential backoff with jitter, retry budget, and per-host concurrency gates)
    /// over arbitrary HTTP-like clients.
    /// </summary>
    public static class GenericResilienceExecutor
    {
        public static Task<TResponse> ExecuteWithResilienceAsync<TRequest, TResponse>(
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
            return ExecuteWithResilienceAsync(
                request,
                sendAsync,
                cloneRequestAsync,
                getHost,
                getStatusCode,
                getRetryAfterDelay,
                maxRetries,
                retryBudget,
                maxConcurrencyPerHost,
                perRequestTimeout: null,
                cancellationToken);
        }

        public static Task<TResponse> ExecuteWithResilienceAsync<TRequest, TResponse>(
            TRequest request,
            Func<TRequest, CancellationToken, Task<TResponse>> sendAsync,
            Func<TRequest, Task<TRequest>> cloneRequestAsync,
            Func<TRequest, string?> getHost,
            Func<TResponse, int> getStatusCode,
            Func<TResponse, TimeSpan?> getRetryAfterDelay,
            int maxRetries,
            TimeSpan? retryBudget,
            int maxConcurrencyPerHost,
            TimeSpan? perRequestTimeout,
            CancellationToken cancellationToken)
        {
            return ExecuteWithResilienceAsyncCore(
                request,
                sendAsync,
                cloneRequestAsync,
                getHost,
                getStatusCode,
                getRetryAfterDelay,
                maxRetries,
                retryBudget,
                maxConcurrencyPerHost,
                perRequestTimeout,
                cancellationToken);
        }

        private static async Task<TResponse> ExecuteWithResilienceAsyncCore<TRequest, TResponse>(
            TRequest request,
            Func<TRequest, CancellationToken, Task<TResponse>> sendAsync,
            Func<TRequest, Task<TRequest>> cloneRequestAsync,
            Func<TRequest, string?> getHost,
            Func<TResponse, int> getStatusCode,
            Func<TResponse, TimeSpan?> getRetryAfterDelay,
            int maxRetries,
            TimeSpan? retryBudget,
            int maxConcurrencyPerHost,
            TimeSpan? perRequestTimeout,
            CancellationToken cancellationToken)
        {
            if (sendAsync == null) throw new ArgumentNullException(nameof(sendAsync));
            if (cloneRequestAsync == null) throw new ArgumentNullException(nameof(cloneRequestAsync));
            if (getHost == null) throw new ArgumentNullException(nameof(getHost));
            if (getStatusCode == null) throw new ArgumentNullException(nameof(getStatusCode));
            if (getRetryAfterDelay == null) throw new ArgumentNullException(nameof(getRetryAfterDelay));

            retryBudget ??= TimeSpan.FromSeconds(60);

            using var timeoutCts = perRequestTimeout.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            if (perRequestTimeout.HasValue)
            {
                timeoutCts!.CancelAfter(perRequestTimeout.Value);
            }

            var effectiveToken = timeoutCts?.Token ?? cancellationToken;
            var deadline = DateTime.UtcNow + retryBudget.Value;
            var attempt = 0;

            var host = getHost(request);
            var gate = HostGateRegistry.Get(host, Math.Max(1, maxConcurrencyPerHost));
            await gate.WaitAsync(effectiveToken).ConfigureAwait(false);

            try
            {
                while (true)
                {
                    attempt++;

                    var attemptRequest = await cloneRequestAsync(request).ConfigureAwait(false);
                    var response = await sendAsync(attemptRequest, effectiveToken).ConfigureAwait(false);

                    var status = getStatusCode(response);
                    var retryable = status == (int)HttpStatusCode.RequestTimeout
                                   || status == (int)HttpStatusCode.TooManyRequests
                                   || (status >= 500 && status <= 599);

                    if (!retryable || attempt >= maxRetries)
                    {
                        return response;
                    }

                    var delay = getRetryAfterDelay(response)
                               ?? TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))) + GetJitter();

                    var now = DateTime.UtcNow;
                    if (now + delay > deadline)
                    {
                        return response;
                    }

                    await Task.Delay(delay, effectiveToken).ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        private static TimeSpan GetJitter()
        {
            var ms = RandomProvider.Next(50, 250);
            return TimeSpan.FromMilliseconds(ms);
        }
    }
}
