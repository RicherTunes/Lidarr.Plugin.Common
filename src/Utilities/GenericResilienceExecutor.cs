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
            ResiliencePolicy policy,
            CancellationToken cancellationToken = default)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));

            return ExecuteWithResilienceAsyncCore(
                request,
                sendAsync,
                cloneRequestAsync,
                getHost,
                getStatusCode,
                getRetryAfterDelay,
                policy,
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
            var policy = ResiliencePolicy.Default.With(
                maxRetries: maxRetries,
                retryBudget: retryBudget ?? ResiliencePolicy.Default.RetryBudget,
                maxConcurrencyPerHost: maxConcurrencyPerHost,
                perRequestTimeout: perRequestTimeout ?? ResiliencePolicy.Default.PerRequestTimeout);

            return ExecuteWithResilienceAsync(
                request,
                sendAsync,
                cloneRequestAsync,
                getHost,
                getStatusCode,
                getRetryAfterDelay,
                policy,
                cancellationToken);
        }
        private static async Task<TResponse> ExecuteWithResilienceAsyncCore<TRequest, TResponse>(
            TRequest request,
            Func<TRequest, CancellationToken, Task<TResponse>> sendAsync,
            Func<TRequest, Task<TRequest>> cloneRequestAsync,
            Func<TRequest, string?> getHost,
            Func<TResponse, int> getStatusCode,
            Func<TResponse, TimeSpan?> getRetryAfterDelay,
            ResiliencePolicy policy,
            CancellationToken cancellationToken)
        {
            if (sendAsync == null) throw new ArgumentNullException(nameof(sendAsync));
            if (cloneRequestAsync == null) throw new ArgumentNullException(nameof(cloneRequestAsync));
            if (getHost == null) throw new ArgumentNullException(nameof(getHost));
            if (getStatusCode == null) throw new ArgumentNullException(nameof(getStatusCode));
            if (getRetryAfterDelay == null) throw new ArgumentNullException(nameof(getRetryAfterDelay));
            if (policy == null) throw new ArgumentNullException(nameof(policy));

            using var timeoutCts = policy.PerRequestTimeout.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            if (policy.PerRequestTimeout.HasValue)
            {
                timeoutCts!.CancelAfter(policy.PerRequestTimeout.Value);
            }

            var effectiveToken = timeoutCts?.Token ?? cancellationToken;
            var deadline = DateTime.UtcNow + policy.RetryBudget;
            var attempt = 0;

            var host = getHost(request);
            var gate = HostGateRegistry.Get(host, Math.Max(1, policy.MaxConcurrencyPerHost));
            await gate.WaitAsync(effectiveToken).ConfigureAwait(false);

            try
            {
                while (true)
                {
                    attempt++;

                    var attemptRequest = await cloneRequestAsync(request).ConfigureAwait(false);

                    TResponse response;
                    try
                    {
                        response = await sendAsync(attemptRequest, effectiveToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex) when (policy.PerRequestTimeout.HasValue &&
                                                               timeoutCts!.IsCancellationRequested &&
                                                               !cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"Request exceeded the per-request timeout of {policy.PerRequestTimeout.Value}.",
                            ex);
                    }

                    var status = getStatusCode(response);
                    var retryable = status == (int)HttpStatusCode.RequestTimeout
                                   || status == (int)HttpStatusCode.TooManyRequests
                                   || (status >= 500 && status <= 599);

                    if (!retryable || attempt >= policy.MaxRetries)
                    {
                        return response;
                    }

                    var retryAfter = getRetryAfterDelay(response);
                    var delay = retryAfter ?? policy.ComputeDelay(attempt) + policy.ComputeJitter();

                    var now = DateTime.UtcNow;
                    if (now + delay > deadline)
                    {
                        return response;
                    }

                    if (response is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }

                    await Task.Delay(delay, effectiveToken).ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
