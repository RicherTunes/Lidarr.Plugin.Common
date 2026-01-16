using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Services.Resilience
{
    /// <summary>
    /// Advanced circuit breaker with dual-trip logic (consecutive failures OR failure rate),
    /// minimum throughput guard, and configurable exception classification.
    ///
    /// Designed for WS4.2 parity with Brainarr's circuit breaker semantics.
    /// </summary>
    /// <remarks>
    /// Key features:
    /// - Trips on consecutive failures >= threshold
    /// - Trips on failure rate >= threshold when minimum throughput is met
    /// - Half-open state requires N successes to close, any failure reopens
    /// - TimeProvider injection for deterministic testing
    /// - Pluggable exception classification (IsFailure, IsIgnored)
    /// </remarks>
    public sealed class AdvancedCircuitBreaker : ICircuitBreaker
    {
        private readonly AdvancedCircuitBreakerOptions _options;
        private readonly TimeProvider _timeProvider;
        private readonly CircularBuffer<bool> _operationResults; // true = success, false = failure
        private readonly object _lock = new();

        private CircuitState _state = CircuitState.Closed;
        private int _consecutiveFailures;
        private int _halfOpenSuccessCount;
        private DateTimeOffset _openedAt;
        private DateTimeOffset? _lastFailureAt;

        // Statistics
        private long _totalSuccesses;
        private long _totalFailures;
        private int _timesOpened;

        /// <inheritdoc />
        public string Name { get; }

        /// <inheritdoc />
        public CircuitState State
        {
            get
            {
                EventHandler<CircuitBreakerEventArgs>? handler = null;
                CircuitBreakerEventArgs? args = null;
                CircuitState state;

                lock (_lock)
                {
                    // Check for automatic transition from Open to HalfOpen
                    if (_state == CircuitState.Open && ShouldTransitionToHalfOpen())
                    {
                        handler = StateChanged;
                        args = TransitionToLocked(CircuitState.HalfOpen, "Break duration elapsed");
                    }
                    state = _state;
                }

                InvokeStateChanged(handler, args);
                return state;
            }
        }

        /// <inheritdoc />
        public bool AllowsRequest => State != CircuitState.Open;

        /// <inheritdoc />
        public CircuitBreakerStatistics Statistics
        {
            get
            {
                lock (_lock)
                {
                    return new CircuitBreakerStatistics
                    {
                        TotalSuccesses = _totalSuccesses,
                        TotalFailures = _totalFailures,
                        TimesOpened = _timesOpened,
                        FailuresInWindow = _operationResults.CountWhere(r => !r),
                        LastFailureTime = _lastFailureAt?.UtcDateTime,
                        LastOpenedTime = _timesOpened > 0 ? _openedAt.UtcDateTime : null
                    };
                }
            }
        }

        /// <summary>
        /// Gets the number of consecutive failures (for testing/diagnostics).
        /// </summary>
        public int ConsecutiveFailures
        {
            get { lock (_lock) { return _consecutiveFailures; } }
        }

        /// <summary>
        /// Gets the current failure rate in the sampling window (0.0 to 1.0).
        /// </summary>
        public double FailureRate
        {
            get
            {
                lock (_lock)
                {
                    var count = _operationResults.Count;
                    if (count == 0) return 0;
                    var failures = _operationResults.CountWhere(r => !r);
                    return (double)failures / count;
                }
            }
        }

        /// <inheritdoc />
        public event EventHandler<CircuitBreakerEventArgs> StateChanged;

        /// <summary>
        /// Creates a new AdvancedCircuitBreaker with the specified options and time provider.
        /// </summary>
        /// <param name="name">Name/identifier for this breaker instance.</param>
        /// <param name="options">Configuration options. If null, defaults are used.</param>
        /// <param name="timeProvider">Time provider for deterministic testing. If null, uses system time.</param>
        public AdvancedCircuitBreaker(
            string name,
            AdvancedCircuitBreakerOptions? options = null,
            TimeProvider? timeProvider = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _options = options ?? AdvancedCircuitBreakerOptions.Default;
            _options.Validate();
            _timeProvider = timeProvider ?? TimeProvider.System;
            _operationResults = new CircularBuffer<bool>(_options.SamplingWindowSize);
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
        public async Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken,
            string operationName)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            EnsureCircuitAllowsRequest();

            try
            {
                var result = await operation(cancellationToken).ConfigureAwait(false);
                RecordSuccess();
                return result;
            }
            catch (Exception ex)
            {
                HandleException(ex);
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
        public async Task ExecuteAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken,
            string operationName)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            EnsureCircuitAllowsRequest();

            try
            {
                await operation(cancellationToken).ConfigureAwait(false);
                RecordSuccess();
            }
            catch (Exception ex)
            {
                HandleException(ex);
                throw;
            }
        }

        /// <inheritdoc />
        public void RecordSuccess()
        {
            EventHandler<CircuitBreakerEventArgs>? handler = null;
            CircuitBreakerEventArgs? args = null;

            lock (_lock)
            {
                _totalSuccesses++;
                _consecutiveFailures = 0;
                _operationResults.Add(true);

                if (_state == CircuitState.HalfOpen)
                {
                    _halfOpenSuccessCount++;
                    if (_halfOpenSuccessCount >= _options.HalfOpenSuccessThreshold)
                    {
                        handler = StateChanged;
                        args = TransitionToLocked(CircuitState.Closed, $"Half-open success threshold ({_options.HalfOpenSuccessThreshold}) reached");
                    }
                }
            }

            InvokeStateChanged(handler, args);
        }

        /// <inheritdoc />
        public void RecordFailure()
        {
            EventHandler<CircuitBreakerEventArgs>? handler = null;
            CircuitBreakerEventArgs? args = null;

            lock (_lock)
            {
                _totalFailures++;
                _lastFailureAt = _timeProvider.GetUtcNow();
                _consecutiveFailures++;
                _operationResults.Add(false);

                if (_state == CircuitState.HalfOpen)
                {
                    // Any failure in half-open immediately reopens
                    handler = StateChanged;
                    args = TransitionToLocked(CircuitState.Open, "Failure in half-open state");
                }
                else if (_state == CircuitState.Closed)
                {
                    handler = StateChanged;
                    args = CheckAndTripIfNeededLocked();
                }
            }

            InvokeStateChanged(handler, args);
        }

        /// <inheritdoc />
        public void Reset()
        {
            lock (_lock)
            {
                _state = CircuitState.Closed;
                _consecutiveFailures = 0;
                _halfOpenSuccessCount = 0;
                _operationResults.Clear();
                _totalSuccesses = 0;
                _totalFailures = 0;
                _lastFailureAt = null;
            }
        }

        private void EnsureCircuitAllowsRequest()
        {
            EventHandler<CircuitBreakerEventArgs>? handler = null;
            CircuitBreakerEventArgs? args = null;

            lock (_lock)
            {
                // Check for automatic transition from Open to HalfOpen
                if (_state == CircuitState.Open && ShouldTransitionToHalfOpen())
                {
                    handler = StateChanged;
                    args = TransitionToLocked(CircuitState.HalfOpen, "Break duration elapsed");
                }

                if (_state == CircuitState.Open)
                {
                    var retryAfter = _openedAt.Add(_options.BreakDuration) - _timeProvider.GetUtcNow();
                    throw new CircuitBreakerOpenException(Name, retryAfter > TimeSpan.Zero ? retryAfter : null);
                }
            }

            InvokeStateChanged(handler, args);
        }

        private void HandleException(Exception ex)
        {
            // Check if exception should be ignored entirely
            if (_options.IsIgnored?.Invoke(ex) == true)
            {
                return; // Don't record anything
            }

            // Check if exception counts as a failure
            if (_options.IsFailure(ex))
            {
                RecordFailure();
            }
            // Non-failure exceptions pass through without affecting breaker state
        }

        private CircuitBreakerEventArgs? CheckAndTripIfNeededLocked()
        {
            // Trip on consecutive failures
            if (_consecutiveFailures >= _options.ConsecutiveFailureThreshold)
            {
                return TransitionToLocked(CircuitState.Open, $"Consecutive failures ({_consecutiveFailures}) >= threshold ({_options.ConsecutiveFailureThreshold})");
            }

            // Trip on failure rate (only if minimum throughput met)
            var count = _operationResults.Count;
            if (count >= _options.MinimumThroughput)
            {
                var failures = _operationResults.CountWhere(r => !r);
                var rate = (double)failures / count;
                if (rate >= _options.FailureRateThreshold)
                {
                    return TransitionToLocked(CircuitState.Open, $"Failure rate ({rate:P0}) >= threshold ({_options.FailureRateThreshold:P0}) with throughput ({count}) >= minimum ({_options.MinimumThroughput})");
                }
            }

            return null;
        }

        private bool ShouldTransitionToHalfOpen()
        {
            var elapsed = _timeProvider.GetUtcNow() - _openedAt;
            return elapsed >= _options.BreakDuration;
        }

        private CircuitBreakerEventArgs? TransitionToLocked(CircuitState newState, string reason)
        {
            var previousState = _state;
            if (previousState == newState)
                return null;

            _state = newState;

            if (newState == CircuitState.Open)
            {
                _openedAt = _timeProvider.GetUtcNow();
                _timesOpened++;
                _halfOpenSuccessCount = 0;
            }
            else if (newState == CircuitState.HalfOpen)
            {
                _halfOpenSuccessCount = 0;
            }
            else if (newState == CircuitState.Closed)
            {
                _consecutiveFailures = 0;
                _halfOpenSuccessCount = 0;
            }

            return new CircuitBreakerEventArgs(Name, previousState, newState, reason);
        }

        private void InvokeStateChanged(EventHandler<CircuitBreakerEventArgs>? handler, CircuitBreakerEventArgs? args)
        {
            if (handler == null || args == null)
                return;

            try
            {
                handler.Invoke(this, args);
            }
            catch
            {
                // Intentionally swallow errors from state change handlers.
            }
        }
    }
}
