using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Centralized retry and error handling utilities for streaming service plugins.
    /// This class provides the single source of truth for all retry logic.
    /// </summary>
    public static class RetryUtilities
    {
        /// <summary>
        /// Determines if an exception is retryable based on common patterns.
        /// </summary>
        public static bool IsRetryableException(Exception ex)
        {
            if (ex == null)
                return false;

            // Network-related exceptions
            if (ex is HttpRequestException ||
                ex is TaskCanceledException ||
                ex is OperationCanceledException ||
                ex is TimeoutException ||
                ex is System.Net.Sockets.SocketException ||
                ex is System.IO.IOException)
            {
                return true;
            }

            // Check for specific HTTP status codes in WebException
            if (ex is WebException webEx)
            {
                if (webEx.Response is HttpWebResponse response)
                {
                    return IsRetryableStatusCode(response.StatusCode);
                }
                return true; // Most WebExceptions are retryable
            }

            // Check inner exception
            if (ex.InnerException != null)
            {
                return IsRetryableException(ex.InnerException);
            }

            // Check for specific error messages
            var message = ex.Message?.ToLowerInvariant() ?? "";
            return message.Contains("timeout") ||
                   message.Contains("connection") ||
                   message.Contains("network") ||
                   message.Contains("temporarily") ||
                   message.Contains("rate limit") ||
                   message.Contains("too many requests");
        }

        /// <summary>
        /// Determines if an HTTP status code is retryable.
        /// </summary>
        public static bool IsRetryableStatusCode(HttpStatusCode statusCode)
        {
            switch (statusCode)
            {
                case HttpStatusCode.RequestTimeout:        // 408
                case HttpStatusCode.TooManyRequests:        // 429
                case HttpStatusCode.InternalServerError:    // 500
                case HttpStatusCode.BadGateway:             // 502
                case HttpStatusCode.ServiceUnavailable:     // 503
                case HttpStatusCode.GatewayTimeout:         // 504
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Executes an action with exponential backoff retry logic.
        /// </summary>
        public static async Task<T> ExecuteWithRetryAsync<T>(
            Func<Task<T>> action,
            int maxRetries = 3,
            int initialDelayMs = 1000,
            string operationName = null,
            Action<Exception, int, string> onRetry = null)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            var attempt = 0;
            var delay = initialDelayMs;

            while (true)
            {
                try
                {
                    attempt++;
                    return await action();
                }
                catch (Exception ex) when (attempt < maxRetries && IsRetryableException(ex))
                {
                    var opName = operationName ?? "operation";
                    onRetry?.Invoke(ex, attempt, $"Attempt {attempt} of {maxRetries} failed for {opName}. Retrying in {delay}ms...");

                    // Add jitter to prevent thundering herd. Use Random.Shared (thread-safe,
                    // well-mixed) — per-call `new Random()` seeded from system clock would
                    // produce the same jitter on near-simultaneous retries from concurrent
                    // callers, defeating the "prevent thundering herd" intent.
                    var jitter = Random.Shared.Next(0, 500);
                    await Task.Delay(delay + jitter);
                    delay = Math.Min(delay * 2, 30000); // Exponential backoff with max 30s cap
                }
                catch (Exception ex) when (attempt >= maxRetries)
                {
                    var opName = operationName ?? "operation";
                    onRetry?.Invoke(ex, maxRetries, $"All {maxRetries} attempts failed for {opName}");
                    throw;
                }
            }
        }

        /// <summary>
        /// Executes an action with simple retry logic (no exponential backoff).
        /// </summary>
        public static async Task<T> SimpleRetryAsync<T>(
            Func<Task<T>> action,
            int maxRetries = 3,
            int delayMs = 1000)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    return await action();
                }
                catch (Exception ex) when (i < maxRetries - 1 && IsRetryableException(ex))
                {
                    await Task.Delay(delayMs);
                }
            }

            // Final attempt without catching
            return await action();
        }

        /// <summary>
        /// Executes an action with timeout and retry logic.
        /// </summary>
        public static async Task<T> ExecuteWithTimeoutAndRetryAsync<T>(
            Func<CancellationToken, Task<T>> action,
            TimeSpan timeout,
            int maxRetries = 3,
            string operationName = null,
            Action<Exception, int, string> onRetry = null)
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                using (var cts = new CancellationTokenSource(timeout))
                {
                    try
                    {
                        return await action(cts.Token);
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        throw new TimeoutException($"Operation timed out after {timeout.TotalSeconds} seconds");
                    }
                }
            }, maxRetries, 1000, operationName, onRetry);
        }

        // Three nested types — CircuitBreaker, CircuitBreakerOpenException, RateLimiter —
        // were removed (~120 LOC). They had zero callers across Common and all 4 plugins;
        // CircuitBreaker also had non-atomic `_failureCount++` and unlocked `_state` access
        // which made it a correctness landmine if a plugin had ever picked it up. The
        // canonical successors live in:
        //   * `Lidarr.Plugin.Common.Services.Resilience.CircuitBreaker` + `ICircuitBreaker`
        //   * `Lidarr.Plugin.Common.Services.Resilience.CircuitBreakerOpenException`
        //   * `Lidarr.Plugin.Common.Services.Performance.UniversalAdaptiveRateLimiter`
        // — all with thread-safe state, proper interfaces, options classes, and full
        // test coverage.
    }
}