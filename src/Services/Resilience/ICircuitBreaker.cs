using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Services.Resilience
{
    /// <summary>
    /// Defines the contract for a circuit breaker implementation.
    /// Circuit breakers prevent cascading failures by failing fast when a service is unhealthy.
    /// </summary>
    /// <remarks>
    /// The circuit breaker pattern has three states:
    /// - Closed: Normal operation, requests pass through
    /// - Open: Failure threshold exceeded, requests fail immediately
    /// - HalfOpen: Testing if service has recovered
    ///
    /// State transitions:
    /// - Closed → Open: When failure threshold is exceeded within the window
    /// - Open → HalfOpen: After the configured timeout duration
    /// - HalfOpen → Closed: When a test request succeeds
    /// - HalfOpen → Open: When a test request fails
    /// </remarks>
    public interface ICircuitBreaker
    {
        /// <summary>
        /// Gets the name/identifier of this circuit breaker instance.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the current state of the circuit breaker.
        /// </summary>
        CircuitState State { get; }

        /// <summary>
        /// Gets whether the circuit breaker allows requests to pass through.
        /// True when state is Closed or HalfOpen.
        /// </summary>
        bool AllowsRequest { get; }

        /// <summary>
        /// Gets statistics about circuit breaker operations.
        /// </summary>
        CircuitBreakerStatistics Statistics { get; }

        /// <summary>
        /// Raised when the circuit breaker state changes.
        /// </summary>
        event EventHandler<CircuitBreakerEventArgs> StateChanged;

        /// <summary>
        /// Executes an operation through the circuit breaker.
        /// </summary>
        /// <typeparam name="T">The return type of the operation.</typeparam>
        /// <param name="operation">The operation to execute.</param>
        /// <returns>The result of the operation.</returns>
        /// <exception cref="CircuitBreakerOpenException">Thrown when the circuit is open.</exception>
        Task<T> ExecuteAsync<T>(Func<Task<T>> operation);

        /// <summary>
        /// Executes an operation through the circuit breaker with an operation name for logging.
        /// </summary>
        /// <typeparam name="T">The return type of the operation.</typeparam>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="operationName">Human-readable name for logging and diagnostics.</param>
        /// <returns>The result of the operation.</returns>
        /// <exception cref="CircuitBreakerOpenException">Thrown when the circuit is open.</exception>
        Task<T> ExecuteAsync<T>(Func<Task<T>> operation, string operationName);

        /// <summary>
        /// Executes an operation through the circuit breaker with cancellation support.
        /// </summary>
        /// <typeparam name="T">The return type of the operation.</typeparam>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The result of the operation.</returns>
        /// <exception cref="CircuitBreakerOpenException">Thrown when the circuit is open.</exception>
        Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);

        /// <summary>
        /// Executes an operation through the circuit breaker with cancellation support and operation name.
        /// </summary>
        /// <typeparam name="T">The return type of the operation.</typeparam>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="operationName">Human-readable name for logging and diagnostics.</param>
        /// <returns>The result of the operation.</returns>
        /// <exception cref="CircuitBreakerOpenException">Thrown when the circuit is open.</exception>
        Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken, string operationName);

        /// <summary>
        /// Executes a void operation through the circuit breaker.
        /// </summary>
        /// <param name="operation">The operation to execute.</param>
        /// <exception cref="CircuitBreakerOpenException">Thrown when the circuit is open.</exception>
        Task ExecuteAsync(Func<Task> operation);

        /// <summary>
        /// Executes a void operation through the circuit breaker with an operation name for logging.
        /// </summary>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="operationName">Human-readable name for logging and diagnostics.</param>
        /// <exception cref="CircuitBreakerOpenException">Thrown when the circuit is open.</exception>
        Task ExecuteAsync(Func<Task> operation, string operationName);

        /// <summary>
        /// Executes a void operation through the circuit breaker with cancellation support.
        /// </summary>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <exception cref="CircuitBreakerOpenException">Thrown when the circuit is open.</exception>
        Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken);

        /// <summary>
        /// Executes a void operation through the circuit breaker with cancellation support and operation name.
        /// </summary>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="operationName">Human-readable name for logging and diagnostics.</param>
        /// <exception cref="CircuitBreakerOpenException">Thrown when the circuit is open.</exception>
        Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken, string operationName);

        /// <summary>
        /// Manually records a successful operation.
        /// </summary>
        void RecordSuccess();

        /// <summary>
        /// Manually records a failed operation.
        /// </summary>
        void RecordFailure();

        /// <summary>
        /// Resets the circuit breaker to its initial closed state.
        /// </summary>
        void Reset();
    }

    /// <summary>
    /// Represents the state of a circuit breaker.
    /// </summary>
    public enum CircuitState
    {
        /// <summary>
        /// Circuit is closed - requests pass through normally.
        /// </summary>
        Closed,

        /// <summary>
        /// Circuit is open - requests fail immediately.
        /// </summary>
        Open,

        /// <summary>
        /// Circuit is half-open - testing if service has recovered.
        /// </summary>
        HalfOpen
    }

    /// <summary>
    /// Statistics tracked by the circuit breaker.
    /// </summary>
    public class CircuitBreakerStatistics
    {
        /// <summary>
        /// Total number of successful operations.
        /// </summary>
        public long TotalSuccesses { get; set; }

        /// <summary>
        /// Total number of failed operations.
        /// </summary>
        public long TotalFailures { get; set; }

        /// <summary>
        /// Number of times the circuit has been opened.
        /// </summary>
        public int TimesOpened { get; set; }

        /// <summary>
        /// Number of failures in the current sliding window.
        /// </summary>
        public int FailuresInWindow { get; set; }

        /// <summary>
        /// Timestamp of the last failure.
        /// </summary>
        public DateTime? LastFailureTime { get; set; }

        /// <summary>
        /// Timestamp when the circuit was last opened.
        /// </summary>
        public DateTime? LastOpenedTime { get; set; }

        /// <summary>
        /// Total number of operations (successes + failures).
        /// </summary>
        public long TotalOperations => TotalSuccesses + TotalFailures;

        /// <summary>
        /// Success rate as a percentage (0-100).
        /// </summary>
        public double SuccessRate => TotalOperations > 0
            ? (double)TotalSuccesses / TotalOperations * 100
            : 100.0;

        /// <summary>
        /// Creates a copy of the statistics.
        /// </summary>
        public CircuitBreakerStatistics Clone()
        {
            return new CircuitBreakerStatistics
            {
                TotalSuccesses = TotalSuccesses,
                TotalFailures = TotalFailures,
                TimesOpened = TimesOpened,
                FailuresInWindow = FailuresInWindow,
                LastFailureTime = LastFailureTime,
                LastOpenedTime = LastOpenedTime
            };
        }
    }

    /// <summary>
    /// Event arguments for circuit breaker state changes.
    /// </summary>
    public class CircuitBreakerEventArgs : EventArgs
    {
        /// <summary>
        /// Name of the circuit breaker.
        /// </summary>
        public string CircuitName { get; }

        /// <summary>
        /// The previous state before the transition.
        /// </summary>
        public CircuitState PreviousState { get; }

        /// <summary>
        /// The new state after the transition.
        /// </summary>
        public CircuitState NewState { get; }

        /// <summary>
        /// When the state change occurred.
        /// </summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// Optional reason for the state change.
        /// </summary>
        public string Reason { get; }

        public CircuitBreakerEventArgs(
            string circuitName,
            CircuitState previousState,
            CircuitState newState,
            string reason = null)
        {
            CircuitName = circuitName;
            PreviousState = previousState;
            NewState = newState;
            Timestamp = DateTime.UtcNow;
            Reason = reason;
        }
    }

    /// <summary>
    /// Exception thrown when an operation is attempted on an open circuit breaker.
    /// </summary>
    public class CircuitBreakerOpenException : Exception
    {
        /// <summary>
        /// Name of the circuit breaker that rejected the request.
        /// </summary>
        public string CircuitName { get; }

        /// <summary>
        /// Estimated time until the circuit transitions to half-open.
        /// </summary>
        public TimeSpan? RetryAfter { get; }

        public CircuitBreakerOpenException(string circuitName, TimeSpan? retryAfter = null)
            : base($"Circuit breaker '{circuitName}' is open. Service is currently unavailable.")
        {
            CircuitName = circuitName;
            RetryAfter = retryAfter;
        }

        public CircuitBreakerOpenException(string circuitName, string message, TimeSpan? retryAfter = null)
            : base(message)
        {
            CircuitName = circuitName;
            RetryAfter = retryAfter;
        }
    }
}
