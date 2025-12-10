using System;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Resilience;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class CircuitBreakerTests
    {
        [Fact]
        public void CircuitBreaker_StartsInClosedState()
        {
            var cb = new CircuitBreaker("test-closed");
            Assert.Equal(CircuitState.Closed, cb.State);
            Assert.True(cb.AllowsRequest);
        }

        [Fact]
        public async Task ExecuteAsync_SuccessfulOperation_ReturnValue()
        {
            var cb = new CircuitBreaker("test-success");
            var result = await cb.ExecuteAsync(() => Task.FromResult(42));
            Assert.Equal(42, result);
            Assert.Equal(CircuitState.Closed, cb.State);
        }

        [Fact]
        public async Task ExecuteAsync_WithOperationName_SuccessfulOperation()
        {
            var cb = new CircuitBreaker("test-opname-success");
            var result = await cb.ExecuteAsync(() => Task.FromResult("test"), "TestOperation");
            Assert.Equal("test", result);
            Assert.Equal(CircuitState.Closed, cb.State);
        }

        [Fact]
        public async Task ExecuteAsync_FailuresOpenCircuit()
        {
            var options = new CircuitBreakerOptions
            {
                FailureThreshold = 3,
                SlidingWindowSize = 5,
                OpenDuration = TimeSpan.FromSeconds(30)
            };
            var cb = new CircuitBreaker("test-failures", options);

            // Cause 3 failures
            for (int i = 0; i < 3; i++)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    cb.ExecuteAsync<int>(() => throw new InvalidOperationException("fail")));
            }

            Assert.Equal(CircuitState.Open, cb.State);
            Assert.False(cb.AllowsRequest);
        }

        [Fact]
        public async Task ExecuteAsync_OpenCircuit_ThrowsCircuitBreakerOpenException()
        {
            var options = new CircuitBreakerOptions
            {
                FailureThreshold = 1,
                SlidingWindowSize = 2,
                OpenDuration = TimeSpan.FromSeconds(30)
            };
            var cb = new CircuitBreaker("test-open-throws", options);

            // Open the circuit
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                cb.ExecuteAsync<int>(() => throw new InvalidOperationException("fail")));

            // Try to execute when open
            var ex = await Assert.ThrowsAsync<CircuitBreakerOpenException>(() =>
                cb.ExecuteAsync(() => Task.FromResult(42)));

            Assert.Equal("test-open-throws", ex.CircuitName);
            Assert.NotNull(ex.RetryAfter);
        }

        [Fact]
        public async Task ExecuteAsync_WithOperationName_OpenCircuit_IncludesOperationNameInException()
        {
            var options = new CircuitBreakerOptions
            {
                FailureThreshold = 1,
                SlidingWindowSize = 2,
                OpenDuration = TimeSpan.FromSeconds(30)
            };
            var cb = new CircuitBreaker("test-opname-exception", options);

            // Open the circuit
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                cb.ExecuteAsync<int>(() => throw new InvalidOperationException("fail")));

            // Try to execute with operation name when open
            var ex = await Assert.ThrowsAsync<CircuitBreakerOpenException>(() =>
                cb.ExecuteAsync(() => Task.FromResult(42), "GetUserData"));

            Assert.Equal("test-opname-exception", ex.CircuitName);
            Assert.Contains("GetUserData", ex.Message);
        }

        [Fact]
        public async Task ExecuteAsync_HalfOpenTransition_AfterTimeout()
        {
            var options = new CircuitBreakerOptions
            {
                FailureThreshold = 1,
                SlidingWindowSize = 2,
                OpenDuration = TimeSpan.FromMilliseconds(100),
                SuccessThresholdInHalfOpen = 1
            };
            var cb = new CircuitBreaker("test-halfopen", options);

            // Open the circuit
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                cb.ExecuteAsync<int>(() => throw new InvalidOperationException("fail")));

            Assert.Equal(CircuitState.Open, cb.State);

            // Wait for open duration
            await Task.Delay(150);

            // Accessing State should trigger transition to HalfOpen
            Assert.Equal(CircuitState.HalfOpen, cb.State);
            Assert.True(cb.AllowsRequest);
        }

        [Fact]
        public async Task ExecuteAsync_HalfOpenSuccess_ClosesCircuit()
        {
            var options = new CircuitBreakerOptions
            {
                FailureThreshold = 1,
                SlidingWindowSize = 2,
                OpenDuration = TimeSpan.FromMilliseconds(50),
                SuccessThresholdInHalfOpen = 1
            };
            var cb = new CircuitBreaker("test-halfopen-close", options);

            // Open the circuit
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                cb.ExecuteAsync<int>(() => throw new InvalidOperationException("fail")));

            // Wait for half-open
            await Task.Delay(100);

            // Successful operation should close the circuit
            var result = await cb.ExecuteAsync(() => Task.FromResult(42));
            Assert.Equal(42, result);
            Assert.Equal(CircuitState.Closed, cb.State);
        }

        [Fact]
        public async Task ExecuteAsync_HalfOpenFailure_ReopensCircuit()
        {
            var options = new CircuitBreakerOptions
            {
                FailureThreshold = 1,
                SlidingWindowSize = 2,
                OpenDuration = TimeSpan.FromMilliseconds(50),
                SuccessThresholdInHalfOpen = 1
            };
            var cb = new CircuitBreaker("test-halfopen-reopen", options);

            // Open the circuit
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                cb.ExecuteAsync<int>(() => throw new InvalidOperationException("fail")));

            // Wait for half-open
            await Task.Delay(100);

            // Failure in half-open should reopen
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                cb.ExecuteAsync<int>(() => throw new InvalidOperationException("fail again")));

            Assert.Equal(CircuitState.Open, cb.State);
        }

        [Fact]
        public async Task ExecuteAsync_WithCancellationToken_PropagatesCancellation()
        {
            var cb = new CircuitBreaker("test-cancel");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                cb.ExecuteAsync(ct =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(42);
                }, cts.Token));

            // Cancellation should not count as failure
            Assert.Equal(CircuitState.Closed, cb.State);
        }

        [Fact]
        public async Task ExecuteAsync_WithCancellationTokenAndOperationName()
        {
            var cb = new CircuitBreaker("test-cancel-opname");
            var result = await cb.ExecuteAsync(
                ct => Task.FromResult(42),
                CancellationToken.None,
                "FetchData");
            Assert.Equal(42, result);
        }

        [Fact]
        public async Task ExecuteAsync_VoidOperation_Success()
        {
            var cb = new CircuitBreaker("test-void");
            var executed = false;

            await cb.ExecuteAsync(() =>
            {
                executed = true;
                return Task.CompletedTask;
            });

            Assert.True(executed);
            Assert.Equal(CircuitState.Closed, cb.State);
        }

        [Fact]
        public async Task ExecuteAsync_VoidOperationWithOperationName()
        {
            var cb = new CircuitBreaker("test-void-opname");
            var executed = false;

            await cb.ExecuteAsync(() =>
            {
                executed = true;
                return Task.CompletedTask;
            }, "ProcessData");

            Assert.True(executed);
        }

        [Fact]
        public void Reset_ClosesCircuitAndClearsFailures()
        {
            var options = new CircuitBreakerOptions
            {
                FailureThreshold = 2,
                SlidingWindowSize = 5,
                OpenDuration = TimeSpan.FromSeconds(30)
            };
            var cb = new CircuitBreaker("test-reset", options);

            // Record failures to open circuit
            cb.RecordFailure();
            cb.RecordFailure();
            Assert.Equal(CircuitState.Open, cb.State);

            cb.Reset();

            Assert.Equal(CircuitState.Closed, cb.State);
            Assert.Equal(0, cb.Statistics.FailuresInWindow);
        }

        [Fact]
        public void Statistics_TracksOperations()
        {
            var cb = new CircuitBreaker("test-stats");

            cb.RecordSuccess();
            cb.RecordSuccess();
            cb.RecordFailure();

            var stats = cb.Statistics;
            Assert.Equal(2, stats.TotalSuccesses);
            Assert.Equal(1, stats.TotalFailures);
            Assert.Equal(3, stats.TotalOperations);
        }

        [Fact]
        public void StateChanged_EventFires_OnTransition()
        {
            var options = new CircuitBreakerOptions
            {
                FailureThreshold = 1,
                SlidingWindowSize = 2,
                OpenDuration = TimeSpan.FromSeconds(30)
            };
            var cb = new CircuitBreaker("test-event", options);

            CircuitState? capturedPreviousState = null;
            CircuitState? capturedNewState = null;

            cb.StateChanged += (sender, args) =>
            {
                capturedPreviousState = args.PreviousState;
                capturedNewState = args.NewState;
            };

            cb.RecordFailure();

            Assert.Equal(CircuitState.Closed, capturedPreviousState);
            Assert.Equal(CircuitState.Open, capturedNewState);
        }

        [Fact]
        public void CircuitBreakerOptions_Validate_ThrowsOnInvalid()
        {
            var options = new CircuitBreakerOptions { FailureThreshold = 0 };
            Assert.Throws<ArgumentException>(() => options.Validate());

            options = new CircuitBreakerOptions { SlidingWindowSize = 2, FailureThreshold = 5 };
            Assert.Throws<ArgumentException>(() => options.Validate());

            options = new CircuitBreakerOptions { OpenDuration = TimeSpan.Zero };
            Assert.Throws<ArgumentException>(() => options.Validate());

            options = new CircuitBreakerOptions { SuccessThresholdInHalfOpen = 0 };
            Assert.Throws<ArgumentException>(() => options.Validate());
        }

        [Fact]
        public void CircuitBreakerOptions_Presets_AreValid()
        {
            CircuitBreakerOptions.Default.Validate();
            CircuitBreakerOptions.Aggressive.Validate();
            CircuitBreakerOptions.Lenient.Validate();
            CircuitBreakerOptions.ForRateLimitedService().Validate();
            CircuitBreakerOptions.ForApiService().Validate();
        }

        [Fact]
        public void CircuitBreakerFactory_CreatesMethods()
        {
            var cb1 = CircuitBreakerFactory.Create("factory-test");
            Assert.NotNull(cb1);
            Assert.Equal("factory-test", cb1.Name);

            var cb2 = CircuitBreakerFactory.CreateAggressive("factory-aggressive");
            Assert.NotNull(cb2);

            var cb3 = CircuitBreakerFactory.CreateLenient("factory-lenient");
            Assert.NotNull(cb3);

            var cb4 = CircuitBreakerFactory.CreateForApi("factory-api");
            Assert.NotNull(cb4);
        }

        [Fact]
        public async Task ShouldHandle_CustomPredicate_FiltersExceptions()
        {
            var options = new CircuitBreakerOptions
            {
                FailureThreshold = 1,
                SlidingWindowSize = 2,
                OpenDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = ex => ex is InvalidOperationException
            };
            var cb = new CircuitBreaker("test-shouldhandle", options);

            // ArgumentException should NOT trigger failure
            await Assert.ThrowsAsync<ArgumentException>(() =>
                cb.ExecuteAsync<int>(() => throw new ArgumentException("not handled")));
            Assert.Equal(CircuitState.Closed, cb.State);

            // InvalidOperationException SHOULD trigger failure
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                cb.ExecuteAsync<int>(() => throw new InvalidOperationException("handled")));
            Assert.Equal(CircuitState.Open, cb.State);
        }
    }
}
