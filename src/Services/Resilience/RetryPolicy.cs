using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Resilience
{
    /// <summary>
    /// Interface for implementing retry policies.
    /// Provides resilient execution of operations that may fail due to transient errors.
    /// </summary>
    public interface IRetryPolicy
    {
        /// <summary>
        /// Executes an operation with retry logic.
        /// </summary>
        /// <typeparam name="T">Return type of the operation.</typeparam>
        /// <param name="operation">The async operation to execute.</param>
        /// <param name="operationName">Human-readable name for logging.</param>
        /// <returns>Result of the operation if successful.</returns>
        /// <exception cref="RetryExhaustedException">Thrown when all retry attempts are exhausted.</exception>
        Task<T> ExecuteAsync<T>(Func<Task<T>> operation, string operationName);

        /// <summary>
        /// Executes an operation with retry logic and cancellation support.
        /// </summary>
        /// <typeparam name="T">Return type of the operation.</typeparam>
        /// <param name="operation">The async operation to execute.</param>
        /// <param name="operationName">Human-readable name for logging.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Result of the operation if successful.</returns>
        Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, string operationName, CancellationToken cancellationToken);

        /// <summary>
        /// Executes a void operation with retry logic.
        /// </summary>
        /// <param name="operation">The async operation to execute.</param>
        /// <param name="operationName">Human-readable name for logging.</param>
        Task ExecuteAsync(Func<Task> operation, string operationName);
    }

    /// <summary>
    /// Configuration options for retry policies.
    /// </summary>
    public class RetryPolicyOptions
    {
        /// <summary>
        /// Maximum number of retry attempts.
        /// Default: 3
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Initial delay between retries.
        /// Default: 500ms
        /// </summary>
        public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Maximum delay cap for exponential backoff.
        /// Default: 60 seconds
        /// </summary>
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Whether to use jitter in delay calculation to prevent thundering herd.
        /// Default: true
        /// </summary>
        public bool UseJitter { get; set; } = true;

        /// <summary>
        /// Optional predicate to determine if an exception should trigger a retry.
        /// Default: All exceptions except TaskCanceledException are retried.
        /// </summary>
        public Func<Exception, bool> ShouldRetry { get; set; }

        /// <summary>
        /// Default options for general use.
        /// </summary>
        public static RetryPolicyOptions Default => new RetryPolicyOptions();

        /// <summary>
        /// Aggressive retry options for critical operations.
        /// </summary>
        public static RetryPolicyOptions Aggressive => new RetryPolicyOptions
        {
            MaxRetries = 5,
            InitialDelay = TimeSpan.FromMilliseconds(200),
            MaxDelay = TimeSpan.FromSeconds(30)
        };

        /// <summary>
        /// Conservative retry options to minimize server load.
        /// </summary>
        public static RetryPolicyOptions Conservative => new RetryPolicyOptions
        {
            MaxRetries = 2,
            InitialDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromMinutes(2)
        };

        /// <summary>
        /// Options optimized for rate-limited APIs.
        /// </summary>
        public static RetryPolicyOptions ForRateLimitedApi => new RetryPolicyOptions
        {
            MaxRetries = 3,
            InitialDelay = TimeSpan.FromSeconds(2),
            MaxDelay = TimeSpan.FromMinutes(1),
            UseJitter = true
        };
    }

    /// <summary>
    /// Implementation of exponential backoff retry policy with jitter.
    /// </summary>
    /// <remarks>
    /// Exponential Backoff Algorithm with Full Jitter:
    /// - Attempt 1: No delay
    /// - Attempt 2: Random(0, initialDelay)
    /// - Attempt 3: Random(0, initialDelay * 2)
    /// - Attempt 4: Random(0, initialDelay * 4)
    /// - etc.
    ///
    /// Benefits:
    /// - Prevents thundering herd problems when multiple instances retry simultaneously
    /// - Gives failing services time to recover
    /// - Full jitter provides better distribution than decorrelated jitter
    /// - Balances quick recovery with system stability
    ///
    /// Use cases:
    /// - AI provider API calls (network timeouts, rate limits)
    /// - Music metadata validation requests
    /// - Provider health checks
    ///
    /// Non-retryable scenarios:
    /// - TaskCanceledException (user cancellation)
    /// - Authentication errors (permanent failures - if configured)
    /// </remarks>
    public class ExponentialBackoffRetryPolicy : IRetryPolicy
    {
        private readonly ILogger _logger;
        private readonly RetryPolicyOptions _options;
        private readonly Random _random;

        /// <summary>
        /// Creates a new ExponentialBackoffRetryPolicy with default options.
        /// </summary>
        /// <param name="logger">Logger for diagnostic information.</param>
        public ExponentialBackoffRetryPolicy(ILogger logger)
            : this(logger, RetryPolicyOptions.Default)
        {
        }

        /// <summary>
        /// Creates a new ExponentialBackoffRetryPolicy with custom options.
        /// </summary>
        /// <param name="logger">Logger for diagnostic information.</param>
        /// <param name="options">Retry policy options.</param>
        public ExponentialBackoffRetryPolicy(ILogger logger, RetryPolicyOptions options)
        {
            _logger = logger;
            _options = options ?? RetryPolicyOptions.Default;
            // Use a deterministic seed based on environment for reproducibility in tests
            _random = new Random(unchecked(Environment.TickCount));
        }

        /// <inheritdoc />
        public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, string operationName)
        {
            return await ExecuteAsync(_ => operation(), operationName, CancellationToken.None).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, string operationName, CancellationToken cancellationToken)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            Exception? lastException = null;

            for (int attempt = 0; attempt < _options.MaxRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (attempt > 0)
                    {
                        var delay = CalculateDelay(attempt);
                        _logger?.LogInformation(
                            "Retry {Attempt}/{MaxRetries} for {Operation} after {Delay:F2}s delay",
                            attempt, _options.MaxRetries, operationName, delay.TotalSeconds);
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }

                    return await operation(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Don't retry on cancellation
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;

                    if (!ShouldRetryException(ex))
                    {
                        _logger?.LogWarning(ex,
                            "Non-retryable exception for {Operation}: {Message}",
                            operationName, ex.Message);
                        throw;
                    }

                    _logger?.LogWarning(
                        "Attempt {Attempt}/{MaxRetries} failed for {Operation}: {Message}",
                        attempt + 1, _options.MaxRetries, operationName, ex.Message);

                    if (attempt == _options.MaxRetries - 1)
                    {
                        _logger?.LogError(ex,
                            "All {MaxRetries} attempts failed for {Operation}",
                            _options.MaxRetries, operationName);
                    }
                }
            }

            throw new RetryExhaustedException(
                $"Operation '{operationName}' failed after {_options.MaxRetries} attempts",
                lastException);
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(Func<Task> operation, string operationName)
        {
            await ExecuteAsync(async () =>
            {
                await operation().ConfigureAwait(false);
                return true;
            }, operationName).ConfigureAwait(false);
        }

        private TimeSpan CalculateDelay(int attempt)
        {
            // Prevent integer overflow with cap
            var multiplier = Math.Min(Math.Pow(2, attempt - 1), 1024);
            var baseDelayMs = Math.Min(_options.InitialDelay.TotalMilliseconds * multiplier, _options.MaxDelay.TotalMilliseconds);

            if (_options.UseJitter)
            {
                // Full jitter: random between 0 and calculated delay
                var jitterMs = _random.Next(0, (int)Math.Max(1, baseDelayMs));
                return TimeSpan.FromMilliseconds(jitterMs);
            }

            return TimeSpan.FromMilliseconds(baseDelayMs);
        }

        private bool ShouldRetryException(Exception ex)
        {
            // Use custom predicate if provided
            if (_options.ShouldRetry != null)
                return _options.ShouldRetry(ex);

            // Default: retry all exceptions except cancellation
            return !(ex is OperationCanceledException);
        }
    }

    /// <summary>
    /// Exception thrown when all retry attempts have been exhausted.
    /// </summary>
    public class RetryExhaustedException : Exception
    {
        /// <summary>
        /// The number of attempts that were made.
        /// </summary>
        public int AttemptsCount { get; }

        public RetryExhaustedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public RetryExhaustedException(string message, Exception innerException, int attemptsCount)
            : base(message, innerException)
        {
            AttemptsCount = attemptsCount;
        }
    }

    /// <summary>
    /// Factory for creating retry policy instances.
    /// </summary>
    public static class RetryPolicyFactory
    {
        /// <summary>
        /// Creates a retry policy with default options.
        /// </summary>
        public static IRetryPolicy Create(ILogger logger = null)
        {
            return new ExponentialBackoffRetryPolicy(logger);
        }

        /// <summary>
        /// Creates a retry policy with custom options.
        /// </summary>
        public static IRetryPolicy Create(RetryPolicyOptions options, ILogger logger = null)
        {
            return new ExponentialBackoffRetryPolicy(logger, options);
        }

        /// <summary>
        /// Creates an aggressive retry policy for critical operations.
        /// </summary>
        public static IRetryPolicy CreateAggressive(ILogger logger = null)
        {
            return new ExponentialBackoffRetryPolicy(logger, RetryPolicyOptions.Aggressive);
        }

        /// <summary>
        /// Creates a conservative retry policy to minimize server load.
        /// </summary>
        public static IRetryPolicy CreateConservative(ILogger logger = null)
        {
            return new ExponentialBackoffRetryPolicy(logger, RetryPolicyOptions.Conservative);
        }

        /// <summary>
        /// Creates a retry policy optimized for rate-limited APIs.
        /// </summary>
        public static IRetryPolicy CreateForRateLimitedApi(ILogger logger = null)
        {
            return new ExponentialBackoffRetryPolicy(logger, RetryPolicyOptions.ForRateLimitedApi);
        }
    }
}
