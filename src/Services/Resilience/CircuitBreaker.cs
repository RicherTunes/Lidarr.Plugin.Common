using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Resilience
{
    /// <summary>
    /// Configuration options for a circuit breaker instance.
    /// </summary>
    public class CircuitBreakerOptions
    {
        /// <summary>
        /// Number of failures within the sliding window before opening the circuit.
        /// Default: 5
        /// </summary>
        public int FailureThreshold { get; set; } = 5;

        /// <summary>
        /// Size of the sliding window for tracking failures (in number of operations).
        /// Default: 10
        /// </summary>
        public int SlidingWindowSize { get; set; } = 10;

        /// <summary>
        /// How long the circuit stays open before transitioning to half-open.
        /// Default: 30 seconds
        /// </summary>
        public TimeSpan OpenDuration { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Number of successful operations in half-open state before closing the circuit.
        /// Default: 2
        /// </summary>
        public int SuccessThresholdInHalfOpen { get; set; } = 2;

        /// <summary>
        /// Optional predicate to determine if an exception should count as a failure.
        /// Default: All exceptions are counted as failures.
        /// </summary>
        public Func<Exception, bool> ShouldHandle { get; set; }

        /// <summary>
        /// Default options for general use.
        /// Threshold: 5 failures, Window: 10, Open: 30s
        /// </summary>
        public static CircuitBreakerOptions Default => new CircuitBreakerOptions();

        /// <summary>
        /// Aggressive options for services that need quick failure detection.
        /// Threshold: 3 failures, Window: 5, Open: 60s
        /// </summary>
        public static CircuitBreakerOptions Aggressive => new CircuitBreakerOptions
        {
            FailureThreshold = 3,
            SlidingWindowSize = 5,
            OpenDuration = TimeSpan.FromSeconds(60),
            SuccessThresholdInHalfOpen = 1
        };

        /// <summary>
        /// Lenient options for services that can tolerate more failures.
        /// Threshold: 10 failures, Window: 20, Open: 15s
        /// </summary>
        public static CircuitBreakerOptions Lenient => new CircuitBreakerOptions
        {
            FailureThreshold = 10,
            SlidingWindowSize = 20,
            OpenDuration = TimeSpan.FromSeconds(15),
            SuccessThresholdInHalfOpen = 3
        };

        /// <summary>
        /// Creates options configured for rate-limited services.
        /// Handles rate limit errors specifically.
        /// </summary>
        public static CircuitBreakerOptions ForRateLimitedService() => new CircuitBreakerOptions
        {
            FailureThreshold = 3,
            SlidingWindowSize = 5,
            OpenDuration = TimeSpan.FromMinutes(1),
            SuccessThresholdInHalfOpen = 1
        };

        /// <summary>
        /// Creates options configured for API services with longer recovery times.
        /// </summary>
        public static CircuitBreakerOptions ForApiService() => new CircuitBreakerOptions
        {
            FailureThreshold = 5,
            SlidingWindowSize = 10,
            OpenDuration = TimeSpan.FromSeconds(45),
            SuccessThresholdInHalfOpen = 2
        };

        /// <summary>
        /// Validates the options and throws if invalid.
        /// </summary>
        public void Validate()
        {
            if (FailureThreshold < 1)
                throw new ArgumentException("FailureThreshold must be at least 1.", nameof(FailureThreshold));
            if (SlidingWindowSize < FailureThreshold)
                throw new ArgumentException("SlidingWindowSize must be at least equal to FailureThreshold.", nameof(SlidingWindowSize));
            if (OpenDuration <= TimeSpan.Zero)
                throw new ArgumentException("OpenDuration must be positive.", nameof(OpenDuration));
            if (SuccessThresholdInHalfOpen < 1)
                throw new ArgumentException("SuccessThresholdInHalfOpen must be at least 1.", nameof(SuccessThresholdInHalfOpen));
        }
    }

    /// <summary>
    /// Thread-safe circuit breaker implementation with sliding window failure tracking.
    /// </summary>
    /// <remarks>
    /// State Machine:
    /// ```
    /// ┌─────────┐  failures >= threshold  ┌────────┐
    /// │ CLOSED  │────────────────────────►│  OPEN  │
    /// └────┬────┘                         └───┬────┘
    ///      │                                  │
    ///      │ success                          │ timeout elapsed
    ///      │                                  ▼
    ///      │                            ┌──────────┐
    ///      └────────────────────────────│HALF-OPEN │
    ///         ◄─────────────────────────┴──────────┘
    ///            success threshold met       │
    ///                                        │ failure
    ///                                        │
    ///                                        └──────► OPEN
    /// ```
    /// </remarks>
    public class CircuitBreaker : ICircuitBreaker
    {
        private readonly ILogger _logger;
        private readonly CircuitBreakerOptions _options;
        private readonly TimeProvider _timeProvider;
        private readonly CircularBuffer<DateTime> _failureTimestamps;
        private readonly object _stateLock = new object();

        private CircuitState _state = CircuitState.Closed;
        private DateTime _openedAt;
        private int _halfOpenSuccesses;
        private CircuitBreakerStatistics _statistics;

        /// <summary>
        /// Gets the current UTC time from the time provider.
        /// </summary>
        private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

        /// <inheritdoc />
        public string Name { get; }

        /// <inheritdoc />
        public CircuitState State
        {
            get
            {
                lock (_stateLock)
                {
                    // Check if we should transition from Open to HalfOpen
                    if (_state == CircuitState.Open && UtcNow - _openedAt >= _options.OpenDuration)
                    {
                        TransitionTo(CircuitState.HalfOpen, "Open duration elapsed");
                    }
                    return _state;
                }
            }
        }

        /// <inheritdoc />
        public bool AllowsRequest
        {
            get
            {
                var state = State; // This may trigger Open -> HalfOpen transition
                return state != CircuitState.Open;
            }
        }

        /// <inheritdoc />
        public CircuitBreakerStatistics Statistics
        {
            get
            {
                lock (_stateLock)
                {
                    return _statistics.Clone();
                }
            }
        }

        /// <inheritdoc />
        public event EventHandler<CircuitBreakerEventArgs> StateChanged;

        /// <summary>
        /// Creates a new circuit breaker with the specified name and options.
        /// </summary>
        /// <param name="name">Unique identifier for this circuit breaker.</param>
        /// <param name="options">Configuration options. Uses defaults if null.</param>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public CircuitBreaker(string name, CircuitBreakerOptions options = null, ILogger logger = null)
            : this(name, options, logger, TimeProvider.System)
        {
        }

        /// <summary>
        /// Creates a new circuit breaker with the specified name, options, and time provider.
        /// </summary>
        /// <param name="name">Unique identifier for this circuit breaker.</param>
        /// <param name="options">Configuration options. Uses defaults if null.</param>
        /// <param name="logger">Optional logger for diagnostics.</param>
        /// <param name="timeProvider">Time provider for testability. Uses system time if null.</param>
        public CircuitBreaker(string name, CircuitBreakerOptions options, ILogger? logger, TimeProvider? timeProvider)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _options = options ?? CircuitBreakerOptions.Default;
            _options.Validate();
            _logger = logger;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _failureTimestamps = new CircularBuffer<DateTime>(_options.SlidingWindowSize);
            _statistics = new CircuitBreakerStatistics();
        }

        /// <inheritdoc />
        public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
        {
            return await ExecuteAsync(_ => operation(), CancellationToken.None, null).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, string operationName)
        {
            return await ExecuteAsync(_ => operation(), CancellationToken.None, operationName).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            return await ExecuteAsync(operation, cancellationToken, null).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken, string operationName)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            EnsureCircuitAllowsRequest(operationName);

            try
            {
                var result = await operation(cancellationToken).ConfigureAwait(false);
                RecordSuccess(operationName);
                return result;
            }
            catch (Exception ex) when (ShouldHandleException(ex))
            {
                RecordFailure(operationName, ex);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(Func<Task> operation)
        {
            await ExecuteAsync(_ => operation(), CancellationToken.None, null).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(Func<Task> operation, string operationName)
        {
            await ExecuteAsync(_ => operation(), CancellationToken.None, operationName).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
        {
            await ExecuteAsync(operation, cancellationToken, null).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken, string operationName)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            EnsureCircuitAllowsRequest(operationName);

            try
            {
                await operation(cancellationToken).ConfigureAwait(false);
                RecordSuccess(operationName);
            }
            catch (Exception ex) when (ShouldHandleException(ex))
            {
                RecordFailure(operationName, ex);
                throw;
            }
        }

        /// <inheritdoc />
        public void RecordSuccess()
        {
            RecordSuccess(null);
        }

        /// <summary>
        /// Records a successful operation with an optional operation name for logging.
        /// </summary>
        private void RecordSuccess(string operationName)
        {
            lock (_stateLock)
            {
                _statistics.TotalSuccesses++;

                if (_state == CircuitState.HalfOpen)
                {
                    _halfOpenSuccesses++;
                    if (!string.IsNullOrEmpty(operationName))
                    {
                        _logger?.LogDebug("Circuit '{Name}' half-open success for '{Operation}' {Count}/{Threshold}",
                            Name, operationName, _halfOpenSuccesses, _options.SuccessThresholdInHalfOpen);
                    }
                    else
                    {
                        _logger?.LogDebug("Circuit '{Name}' half-open success {Count}/{Threshold}",
                            Name, _halfOpenSuccesses, _options.SuccessThresholdInHalfOpen);
                    }

                    if (_halfOpenSuccesses >= _options.SuccessThresholdInHalfOpen)
                    {
                        TransitionTo(CircuitState.Closed, "Success threshold met in half-open state");
                    }
                }
            }
        }

        /// <inheritdoc />
        public void RecordFailure()
        {
            RecordFailure(null, null);
        }

        /// <summary>
        /// Records a failed operation with optional operation name and exception for logging.
        /// </summary>
        private void RecordFailure(string operationName, Exception exception)
        {
            lock (_stateLock)
            {
                _statistics.TotalFailures++;
                _statistics.LastFailureTime = UtcNow;
                _failureTimestamps.Add(UtcNow);
                _statistics.FailuresInWindow = _failureTimestamps.Count;

                // Log the failure with operation context if provided
                if (!string.IsNullOrEmpty(operationName))
                {
                    _logger?.LogWarning("Circuit '{Name}' recorded failure for '{Operation}': {Message}",
                        Name, operationName, exception?.Message ?? "Unknown error");
                }

                if (_state == CircuitState.HalfOpen)
                {
                    // Any failure in half-open immediately opens the circuit
                    var reason = !string.IsNullOrEmpty(operationName)
                        ? $"Failure during half-open test for '{operationName}'"
                        : "Failure during half-open test";
                    TransitionTo(CircuitState.Open, reason);
                }
                else if (_state == CircuitState.Closed)
                {
                    // Check if we've exceeded the failure threshold
                    if (_failureTimestamps.Count >= _options.FailureThreshold)
                    {
                        TransitionTo(CircuitState.Open, $"Failure threshold ({_options.FailureThreshold}) exceeded");
                    }
                }
            }
        }

        /// <inheritdoc />
        public void Reset()
        {
            lock (_stateLock)
            {
                var previousState = _state;
                _state = CircuitState.Closed;
                _failureTimestamps.Clear();
                _halfOpenSuccesses = 0;
                _statistics.FailuresInWindow = 0;

                if (previousState != CircuitState.Closed)
                {
                    _logger?.LogInformation("Circuit '{Name}' manually reset from {PreviousState} to Closed", Name, previousState);
                    OnStateChanged(previousState, CircuitState.Closed, "Manual reset");
                }
            }
        }

        private void EnsureCircuitAllowsRequest(string operationName)
        {
            var currentState = State; // May trigger state transition
            if (currentState == CircuitState.Open)
            {
                var retryAfter = _options.OpenDuration - (UtcNow - _openedAt);
                if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;

                var message = !string.IsNullOrEmpty(operationName)
                    ? $"Circuit breaker '{Name}' is open for operation '{operationName}'. Service is currently unavailable."
                    : $"Circuit breaker '{Name}' is open. Service is currently unavailable.";

                _logger?.LogWarning(message);
                throw new CircuitBreakerOpenException(Name, message, retryAfter);
            }
        }

        private bool ShouldHandleException(Exception ex)
        {
            // Don't count cancellation as a failure
            if (ex is OperationCanceledException)
                return false;

            // Use custom predicate if provided
            if (_options.ShouldHandle != null)
                return _options.ShouldHandle(ex);

            // Default: handle all exceptions
            return true;
        }

        private void TransitionTo(CircuitState newState, string reason)
        {
            var previousState = _state;
            if (previousState == newState)
                return;

            _state = newState;

            switch (newState)
            {
                case CircuitState.Open:
                    _openedAt = UtcNow;
                    _statistics.TimesOpened++;
                    _statistics.LastOpenedTime = _openedAt;
                    _logger?.LogWarning("Circuit '{Name}' OPENED: {Reason}", Name, reason);
                    break;

                case CircuitState.HalfOpen:
                    _halfOpenSuccesses = 0;
                    _logger?.LogInformation("Circuit '{Name}' transitioning to HALF-OPEN: {Reason}", Name, reason);
                    break;

                case CircuitState.Closed:
                    _failureTimestamps.Clear();
                    _halfOpenSuccesses = 0;
                    _statistics.FailuresInWindow = 0;
                    _logger?.LogInformation("Circuit '{Name}' CLOSED: {Reason}", Name, reason);
                    break;
            }

            OnStateChanged(previousState, newState, reason);
        }

        private void OnStateChanged(CircuitState previousState, CircuitState newState, string reason)
        {
            try
            {
                StateChanged?.Invoke(this, new CircuitBreakerEventArgs(Name, previousState, newState, reason));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in circuit breaker state change handler for '{Name}'", Name);
            }
        }
    }

    /// <summary>
    /// Factory for creating circuit breaker instances.
    /// </summary>
    public static class CircuitBreakerFactory
    {
        /// <summary>
        /// Creates a circuit breaker with default options.
        /// </summary>
        public static ICircuitBreaker Create(string name, ILogger logger = null)
        {
            return new CircuitBreaker(name, CircuitBreakerOptions.Default, logger);
        }

        /// <summary>
        /// Creates a circuit breaker with custom options.
        /// </summary>
        public static ICircuitBreaker Create(string name, CircuitBreakerOptions options, ILogger logger = null)
        {
            return new CircuitBreaker(name, options, logger);
        }

        /// <summary>
        /// Creates an aggressive circuit breaker for critical services.
        /// </summary>
        public static ICircuitBreaker CreateAggressive(string name, ILogger logger = null)
        {
            return new CircuitBreaker(name, CircuitBreakerOptions.Aggressive, logger);
        }

        /// <summary>
        /// Creates a lenient circuit breaker for services that can tolerate more failures.
        /// </summary>
        public static ICircuitBreaker CreateLenient(string name, ILogger logger = null)
        {
            return new CircuitBreaker(name, CircuitBreakerOptions.Lenient, logger);
        }

        /// <summary>
        /// Creates a circuit breaker optimized for rate-limited API services.
        /// </summary>
        public static ICircuitBreaker CreateForApi(string name, ILogger logger = null)
        {
            return new CircuitBreaker(name, CircuitBreakerOptions.ForApiService(), logger);
        }
    }
}
