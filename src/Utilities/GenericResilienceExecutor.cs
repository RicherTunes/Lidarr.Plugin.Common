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
#if NET8_0_OR_GREATER
                timeProvider: null,
#endif
                cancellationToken);
        }

#if NET8_0_OR_GREATER
        public static Task<TResponse> ExecuteWithResilienceAsync<TRequest, TResponse>(
            TRequest request,
            Func<TRequest, CancellationToken, Task<TResponse>> sendAsync,
            Func<TRequest, Task<TRequest>> cloneRequestAsync,
            Func<TRequest, string?> getHost,
            Func<TResponse, int> getStatusCode,
            Func<TResponse, TimeSpan?> getRetryAfterDelay,
            ResiliencePolicy policy,
            TimeProvider timeProvider,
            CancellationToken cancellationToken = default)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            if (timeProvider == null) throw new ArgumentNullException(nameof(timeProvider));

            return ExecuteWithResilienceAsyncCore(
                request,
                sendAsync,
                cloneRequestAsync,
                getHost,
                getStatusCode,
                getRetryAfterDelay,
                policy,
                timeProvider,
                cancellationToken);
        }
#endif

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

#if NET8_0_OR_GREATER
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
            TimeProvider timeProvider,
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
                timeProvider,
                cancellationToken);
        }
#endif
        private static async Task<TResponse> ExecuteWithResilienceAsyncCore<TRequest, TResponse>(
            TRequest request,
            Func<TRequest, CancellationToken, Task<TResponse>> sendAsync,
            Func<TRequest, Task<TRequest>> cloneRequestAsync,
            Func<TRequest, string?> getHost,
            Func<TResponse, int> getStatusCode,
            Func<TResponse, TimeSpan?> getRetryAfterDelay,
            ResiliencePolicy policy,
#if NET8_0_OR_GREATER
            TimeProvider? timeProvider,
#endif
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
#if NET8_0_OR_GREATER
            var tp = timeProvider ?? TimeProvider.System;
            var deadline = tp.GetUtcNow().UtcDateTime + policy.RetryBudget;
#else
            var deadline = DateTime.UtcNow + policy.RetryBudget;
#endif
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

                    // Record retry for telemetry
                    DownloadTelemetryContext.RecordRetry((HttpStatusCode)status);

                    var retryAfter = getRetryAfterDelay(response);
                    var delay = retryAfter ?? policy.ComputeDelay(attempt) + policy.ComputeJitter();

#if NET8_0_OR_GREATER
                    var now = tp.GetUtcNow().UtcDateTime;
#else
                    var now = DateTime.UtcNow;
#endif
                    if (now + delay > deadline)
                    {
                        return response;
                    }

                    if (response is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }

#if NET8_0_OR_GREATER
                    await DelayAsync(delay, tp, effectiveToken).ConfigureAwait(false);
#else
                    await Task.Delay(delay, effectiveToken).ConfigureAwait(false);
#endif
                }
            }
            finally
            {
                gate.Release();
            }
        }

#if NET8_0_OR_GREATER
        private static async Task DelayAsync(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)
        {
            if (delay <= TimeSpan.Zero)
            {
                return;
            }

            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var ctr = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            using var timer = timeProvider.CreateTimer(static state => ((TaskCompletionSource<object?>)state!).TrySetResult(null), tcs, delay, Timeout.InfiniteTimeSpan);
            await tcs.Task.ConfigureAwait(false);
        }
#endif
    }
}
