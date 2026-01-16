using System;
using System.Net.Http;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Resilience;
using Lidarr.Plugin.Common.TestKit.Testing;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Tests for AdvancedCircuitBreaker verifying WS4.2 parity with Brainarr's circuit breaker semantics.
    /// All tests are deterministic (no sleeps) using FakeTimeProvider for time control.
    /// </summary>
    public class AdvancedCircuitBreakerTests
    {
        /// <summary>
        /// Test 1: Consecutive failures >= threshold opens the circuit.
        /// Default threshold is 5, so 5 failures should open.
        /// </summary>
        [Fact]
        public void Should_Open_When_ConsecutiveFailures_ReachesThreshold()
        {
            // Arrange
            var options = new AdvancedCircuitBreakerOptions
            {
                ConsecutiveFailureThreshold = 5
            };
            var cb = new AdvancedCircuitBreaker("test-consecutive", options);

            // Act - record 4 failures (not enough to trip)
            for (int i = 0; i < 4; i++)
            {
                cb.RecordFailure();
            }

            // Assert - still closed
            Assert.Equal(CircuitState.Closed, cb.State);
            Assert.Equal(4, cb.ConsecutiveFailures);

            // Act - 5th failure should trip
            cb.RecordFailure();

            // Assert - now open
            Assert.Equal(CircuitState.Open, cb.State);
            Assert.False(cb.AllowsRequest);
        }

        /// <summary>
        /// Test 2: Success resets consecutive failure count.
        /// After recording failures, a success should reset the counter.       
        /// </summary>
        [Fact]
        public void Should_Reset_ConsecutiveFailures_On_Success()
        {
            // Arrange
            var options = new AdvancedCircuitBreakerOptions
            {
                ConsecutiveFailureThreshold = 5
            };
            var cb = new AdvancedCircuitBreaker("test-reset", options);

            // Act - record 4 failures
            for (int i = 0; i < 4; i++)
            {
                cb.RecordFailure();
            }
            Assert.Equal(4, cb.ConsecutiveFailures);

            // Act - record a success
            cb.RecordSuccess();

            // Assert - consecutive failures reset to 0
            Assert.Equal(0, cb.ConsecutiveFailures);
            Assert.Equal(CircuitState.Closed, cb.State);

            // Act - record 4 more failures (shouldn't trip because we reset)
            for (int i = 0; i < 4; i++)
            {
                cb.RecordFailure();
            }

            // Assert - still closed (need 5 consecutive, but we reset)
            Assert.Equal(CircuitState.Closed, cb.State);
            Assert.Equal(4, cb.ConsecutiveFailures);
        }

        /// <summary>
        /// Test 3: Failure rate >= threshold trips when minimum throughput is met.
        /// With 50% failure rate threshold and 10 minimum throughput,
        /// 5 failures out of 10 operations should trip.
        /// </summary>
        [Fact]
        public void Should_Open_When_FailureRate_MeetsThreshold_And_MinimumThroughput_Met()
        {
            // Arrange
            var options = new AdvancedCircuitBreakerOptions
            {
                ConsecutiveFailureThreshold = 100, // High so it doesn't trip on consecutive
                FailureRateThreshold = 0.5, // 50%
                MinimumThroughput = 10,
                SamplingWindowSize = 20
            };
            var cb = new AdvancedCircuitBreaker("test-failure-rate", options);

            // Act - record 4 successes and 4 failures (8 total, below minimum throughput)
            for (int i = 0; i < 4; i++)
            {
                cb.RecordSuccess();
                cb.RecordFailure();
            }

            // Assert - still closed (failure rate is 50% but only 8 operations)
            Assert.Equal(CircuitState.Closed, cb.State);
            Assert.Equal(0.5, cb.FailureRate, precision: 2);

            // Act - record 1 success and 1 failure (now 10 total, meets minimum throughput)
            cb.RecordSuccess();
            cb.RecordFailure(); // This should trip: 5/10 = 50% >= 50% threshold

            // Assert - now open
            Assert.Equal(CircuitState.Open, cb.State);
        }

        /// <summary>
        /// Test 4: Half-open behavior tests:
        /// - After break duration, transitions to HalfOpen
        /// - N consecutive successes closes circuit
        /// - Any failure in half-open reopens
        /// </summary>
        [Fact]
        public void Should_Transition_Open_HalfOpen_Closed_With_TimeProvider()
        {
            // Arrange
            var fakeTime = new FakeTimeProvider();
            var options = new AdvancedCircuitBreakerOptions
            {
                ConsecutiveFailureThreshold = 3,
                BreakDuration = TimeSpan.FromSeconds(30),
                HalfOpenSuccessThreshold = 2
            };
            var cb = new AdvancedCircuitBreaker("test-halfopen", options, fakeTime);

            // Act - trip the circuit
            cb.RecordFailure();
            cb.RecordFailure();
            cb.RecordFailure();
            Assert.Equal(CircuitState.Open, cb.State);

            // Assert - still open before break duration
            fakeTime.Advance(TimeSpan.FromSeconds(29));
            Assert.Equal(CircuitState.Open, cb.State);

            // Act - advance past break duration
            fakeTime.Advance(TimeSpan.FromSeconds(2));

            // Assert - now half-open
            Assert.Equal(CircuitState.HalfOpen, cb.State);
            Assert.True(cb.AllowsRequest);

            // Act - record success but not enough to close
            cb.RecordSuccess();
            Assert.Equal(CircuitState.HalfOpen, cb.State);

            // Act - record another success (meets threshold)
            cb.RecordSuccess();

            // Assert - now closed
            Assert.Equal(CircuitState.Closed, cb.State);

            // Test that failure in half-open reopens
            // First, open it again
            cb.RecordFailure();
            cb.RecordFailure();
            cb.RecordFailure();
            Assert.Equal(CircuitState.Open, cb.State);

            // Advance to half-open
            fakeTime.Advance(TimeSpan.FromSeconds(31));
            Assert.Equal(CircuitState.HalfOpen, cb.State);

            // Record a failure - should reopen
            cb.RecordFailure();
            Assert.Equal(CircuitState.Open, cb.State);
        }

        /// <summary>
        /// Test 5: Exception classification predicates work correctly.
        /// - IsIgnored exceptions are not counted at all
        /// - IsFailure exceptions count as failures
        /// - Other exceptions pass through without affecting state
        /// </summary>
        [Fact]
        public async Task Should_Respect_Exception_Classification_Predicates()
        {
            // Arrange
            var options = new AdvancedCircuitBreakerOptions
            {
                ConsecutiveFailureThreshold = 2,
                IsFailure = ex => ex is HttpRequestException || ex is TimeoutException,
                IsIgnored = ex => ex is OperationCanceledException
            };
            var cb = new AdvancedCircuitBreaker("test-classification", options);

            // Act - OperationCanceledException should be ignored (no effect on state)
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                cb.ExecuteAsync<int>(() => throw new OperationCanceledException()));
            Assert.Equal(0, cb.ConsecutiveFailures);
            Assert.Equal(CircuitState.Closed, cb.State);

            // Act - InvalidOperationException is not a failure type (passes through)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                cb.ExecuteAsync<int>(() => throw new InvalidOperationException("not a failure type")));
            Assert.Equal(0, cb.ConsecutiveFailures);
            Assert.Equal(CircuitState.Closed, cb.State);

            // Act - HttpRequestException IS a failure type
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                cb.ExecuteAsync<int>(() => throw new HttpRequestException("failure")));
            Assert.Equal(1, cb.ConsecutiveFailures);

            // Act - TimeoutException IS a failure type (should trip)
            await Assert.ThrowsAsync<TimeoutException>(() =>
                cb.ExecuteAsync<int>(() => throw new TimeoutException("timeout")));
            Assert.Equal(2, cb.ConsecutiveFailures);
            Assert.Equal(CircuitState.Open, cb.State);
        }

        /// <summary>
        /// Test 6: Minimum throughput guards failure rate even at 100% failure rate.
        /// 9/9 failures (100%) should NOT open circuit (below minimum throughput of 10).
        /// 10/10 failures (100%) should open circuit (meets minimum throughput).
        /// </summary>
        [Fact]
        public void Should_Not_Open_On_FailureRate_When_Below_MinimumThroughput()
        {
            // Arrange
            var options = new AdvancedCircuitBreakerOptions
            {
                ConsecutiveFailureThreshold = 100, // High so consecutive doesn't trip
                FailureRateThreshold = 0.5, // 50%
                MinimumThroughput = 10,
                SamplingWindowSize = 20
            };
            var cb = new AdvancedCircuitBreaker("test-min-throughput-guard", options);

            // Act - record 9 failures (100% failure rate, but below minimum throughput)
            for (int i = 0; i < 9; i++)
            {
                cb.RecordFailure();
            }

            // Assert - still closed despite 100% failure rate (9 < 10 minimum)
            Assert.Equal(CircuitState.Closed, cb.State);
            Assert.Equal(1.0, cb.FailureRate, precision: 2);
            Assert.Equal(9, cb.ConsecutiveFailures);

            // Act - 10th failure (now meets minimum throughput)
            cb.RecordFailure();

            // Assert - now open (10/10 = 100% >= 50% threshold, and 10 >= 10 minimum)
            Assert.Equal(CircuitState.Open, cb.State);
        }

        /// <summary>
        /// Additional test: Options presets are valid.
        /// </summary>
        [Fact]
        public void Should_Validate_Options_Presets()
        {
            AdvancedCircuitBreakerOptions.Default.Validate();
            AdvancedCircuitBreakerOptions.Aggressive.Validate();
            AdvancedCircuitBreakerOptions.Lenient.Validate();
        }

        /// <summary>
        /// Additional test: Options validation catches invalid values.
        /// </summary>
        [Fact]
        public void Should_Throw_When_Options_Invalid()
        {
            var options = new AdvancedCircuitBreakerOptions { ConsecutiveFailureThreshold = 0 };
            Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());

            options = new AdvancedCircuitBreakerOptions { FailureRateThreshold = 1.5 };
            Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());

            options = new AdvancedCircuitBreakerOptions { MinimumThroughput = 0 };
            Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());

            options = new AdvancedCircuitBreakerOptions { SamplingWindowSize = 0 };
            Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());

            options = new AdvancedCircuitBreakerOptions { SamplingWindowSize = 5, MinimumThroughput = 6 };
            Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());

            options = new AdvancedCircuitBreakerOptions { BreakDuration = TimeSpan.FromSeconds(-1) };
            Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());

            options = new AdvancedCircuitBreakerOptions { HalfOpenSuccessThreshold = 0 };
            Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());

            options = new AdvancedCircuitBreakerOptions { IsFailure = null! };
            Assert.Throws<ArgumentNullException>(() => options.Validate());
        }

        /// <summary>
        /// Additional test: Statistics are tracked correctly.
        /// </summary>
        [Fact]
        public void Should_Track_Statistics()
        {
            var options = new AdvancedCircuitBreakerOptions
            {
                ConsecutiveFailureThreshold = 3
            };
            var cb = new AdvancedCircuitBreaker("test-stats", options);

            cb.RecordSuccess();
            cb.RecordSuccess();
            cb.RecordFailure();
            cb.RecordFailure();
            cb.RecordFailure(); // Opens circuit

            var stats = cb.Statistics;
            Assert.Equal(2, stats.TotalSuccesses);
            Assert.Equal(3, stats.TotalFailures);
            Assert.Equal(1, stats.TimesOpened);
            Assert.Equal(3, stats.FailuresInWindow);
        }
    }
}
