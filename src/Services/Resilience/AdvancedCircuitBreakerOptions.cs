using System;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Services.Resilience
{
    /// <summary>
    /// Configuration options for AdvancedCircuitBreaker.
    /// Supports dual-trip logic (consecutive failures OR failure rate) with minimum throughput guard.
    /// </summary>
    public sealed class AdvancedCircuitBreakerOptions
    {
        /// <summary>
        /// Number of consecutive failures required to open the circuit.
        /// Default: 5
        /// </summary>
        public int ConsecutiveFailureThreshold { get; set; } = 5;

        /// <summary>
        /// Failure rate threshold (0.0 to 1.0) that triggers circuit open.
        /// Only applies when MinimumThroughput is met.
        /// Default: 0.5 (50%)
        /// </summary>
        public double FailureRateThreshold { get; set; } = 0.5;

        /// <summary>
        /// Minimum number of operations in the sampling window before failure rate is considered.
        /// Prevents opening on small sample sizes (e.g., 1/2 = 50% but only 2 calls).
        /// Default: 10
        /// </summary>
        public int MinimumThroughput { get; set; } = 10;

        /// <summary>
        /// Size of the sliding window for tracking operations.
        /// Default: 20
        /// </summary>
        public int SamplingWindowSize { get; set; } = 20;

        /// <summary>
        /// Duration the circuit stays open before transitioning to half-open.
        /// Default: 30 seconds
        /// </summary>
        public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Number of consecutive successes in half-open state required to close the circuit.
        /// Default: 3
        /// </summary>
        public int HalfOpenSuccessThreshold { get; set; } = 3;

        /// <summary>
        /// Predicate to determine if an exception should count as a failure.
        /// Return true if the exception should trip the breaker.
        /// Default: TaskCanceledException, TimeoutException, HttpRequestException count as failures.
        /// </summary>
        public Func<Exception, bool> IsFailure { get; set; } = DefaultIsFailure;

        /// <summary>
        /// Predicate to determine if an exception should be ignored (not counted at all).
        /// Takes precedence over IsFailure - if IsIgnored returns true, IsFailure is not called.
        /// Default: null (no exceptions ignored)
        /// </summary>
        public Func<Exception, bool> IsIgnored { get; set; }

        /// <summary>
        /// Default failure classification matching Brainarr behavior.
        /// TaskCanceledException, TimeoutException, and HttpRequestException are treated as failures.
        /// </summary>
        public static bool DefaultIsFailure(Exception ex)
        {
            return ex is TaskCanceledException ||
                   ex is TimeoutException ||
                   ex is System.Net.Http.HttpRequestException;
        }

        /// <summary>
        /// Creates options with default values matching Brainarr's circuit breaker behavior.
        /// </summary>
        public static AdvancedCircuitBreakerOptions Default => new();

        /// <summary>
        /// Creates aggressive options that trip faster and stay open longer.
        /// </summary>
        public static AdvancedCircuitBreakerOptions Aggressive => new()
        {
            ConsecutiveFailureThreshold = 3,
            FailureRateThreshold = 0.3,
            MinimumThroughput = 5,
            BreakDuration = TimeSpan.FromMinutes(5)
        };

        /// <summary>
        /// Creates lenient options that tolerate more failures before tripping.
        /// </summary>
        public static AdvancedCircuitBreakerOptions Lenient => new()
        {
            ConsecutiveFailureThreshold = 10,
            FailureRateThreshold = 0.75,
            MinimumThroughput = 20,
            BreakDuration = TimeSpan.FromSeconds(15)
        };

        /// <summary>
        /// Validates the options and throws if invalid.
        /// </summary>
        public void Validate()
        {
            if (ConsecutiveFailureThreshold < 1)
                throw new ArgumentOutOfRangeException(nameof(ConsecutiveFailureThreshold), "Must be at least 1.");

            if (FailureRateThreshold < 0 || FailureRateThreshold > 1)
                throw new ArgumentOutOfRangeException(nameof(FailureRateThreshold), "Must be between 0.0 and 1.0.");

            if (MinimumThroughput < 1)
                throw new ArgumentOutOfRangeException(nameof(MinimumThroughput), "Must be at least 1.");

            if (SamplingWindowSize < 1)
                throw new ArgumentOutOfRangeException(nameof(SamplingWindowSize), "Must be at least 1.");

            if (MinimumThroughput > SamplingWindowSize)
                throw new ArgumentOutOfRangeException(nameof(MinimumThroughput), "Must be less than or equal to SamplingWindowSize.");

            if (BreakDuration < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(BreakDuration), "Must be non-negative.");

            if (HalfOpenSuccessThreshold < 1)
                throw new ArgumentOutOfRangeException(nameof(HalfOpenSuccessThreshold), "Must be at least 1.");

            if (IsFailure == null)
                throw new ArgumentNullException(nameof(IsFailure), "Failure classifier must be provided.");
        }
    }
}
