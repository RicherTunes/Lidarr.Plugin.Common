using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Resilience
{
    /// <summary>
    /// Wave-2 coverage: <see cref="ExponentialBackoffRetryPolicy"/> had zero direct test
    /// references prior to this file (RetrySemanticsTests/RetrySemanticsTimeProviderTests
    /// cover a different code path -- HttpClientExtensions.ExecuteWithResilienceAsync).
    ///
    /// The class exposes no injectable clock or jitter-source seam: delays go through the
    /// real <see cref="Task.Delay(TimeSpan, CancellationToken)"/> and jitter comes from
    /// <see cref="Random.Shared"/> (see the Wave-56 comment on the constructor). So:
    ///  - jitter bounds are verified by invoking the private CalculateDelay(attempt) helper
    ///    via reflection (deterministic, no sleeping);
    ///  - behavioral tests that must actually go through a delay use millisecond-scale
    ///    InitialDelay/MaxDelay values to avoid slow/flaky wall-clock waits.
    /// </summary>
    public class ExponentialBackoffRetryPolicyTests
    {
        private static readonly MethodInfo CalculateDelayMethod =
            typeof(ExponentialBackoffRetryPolicy).GetMethod("CalculateDelay", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("CalculateDelay method not found via reflection.");

        private static TimeSpan InvokeCalculateDelay(ExponentialBackoffRetryPolicy policy, int attempt)
            => (TimeSpan)CalculateDelayMethod.Invoke(policy, new object[] { attempt })!;

        private sealed class TransientException : Exception
        {
            public TransientException(string message = "transient") : base(message) { }
        }

        // ---------------------------------------------------------------
        // Jitter bounds (deterministic, via reflection on CalculateDelay)
        // ---------------------------------------------------------------

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void CalculateDelay_WithJitter_StaysWithinFullJitterBounds(int attempt)
        {
            var options = new RetryPolicyOptions
            {
                InitialDelay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromSeconds(10),
                UseJitter = true
            };
            var policy = new ExponentialBackoffRetryPolicy(NullLogger.Instance, options);

            var expectedBaseMs = Math.Min(options.InitialDelay.TotalMilliseconds * Math.Pow(2, attempt - 1), options.MaxDelay.TotalMilliseconds);

            // Sample many times: full jitter must always land in [0, baseDelay).
            for (var i = 0; i < 50; i++)
            {
                var delay = InvokeCalculateDelay(policy, attempt);
                Assert.InRange(delay.TotalMilliseconds, 0, expectedBaseMs);
            }
        }

        [Fact]
        public void CalculateDelay_NeverExceedsMaxDelay_EvenAtHighAttemptCounts()
        {
            var options = new RetryPolicyOptions
            {
                InitialDelay = TimeSpan.FromMilliseconds(500),
                MaxDelay = TimeSpan.FromSeconds(5),
                UseJitter = true
            };
            var policy = new ExponentialBackoffRetryPolicy(NullLogger.Instance, options);

            // Attempt 20 would overflow without the cap; the delay must never exceed MaxDelay.
            for (var attempt = 5; attempt <= 20; attempt++)
            {
                var delay = InvokeCalculateDelay(policy, attempt);
                Assert.True(delay.TotalMilliseconds <= options.MaxDelay.TotalMilliseconds,
                    $"attempt {attempt} produced {delay.TotalMilliseconds}ms, exceeding MaxDelay {options.MaxDelay.TotalMilliseconds}ms");
            }
        }

        [Fact]
        public void CalculateDelay_WithoutJitter_IsExactExponentialValue_CappedAtMaxDelay()
        {
            var options = new RetryPolicyOptions
            {
                InitialDelay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromSeconds(1),
                UseJitter = false
            };
            var policy = new ExponentialBackoffRetryPolicy(NullLogger.Instance, options);

            // attempt=1 -> multiplier 2^0=1 -> 100ms
            Assert.Equal(100, InvokeCalculateDelay(policy, 1).TotalMilliseconds);
            // attempt=2 -> multiplier 2^1=2 -> 200ms
            Assert.Equal(200, InvokeCalculateDelay(policy, 2).TotalMilliseconds);
            // attempt=3 -> multiplier 2^2=4 -> 400ms
            Assert.Equal(400, InvokeCalculateDelay(policy, 3).TotalMilliseconds);
            // attempt=6 -> multiplier 2^5=32 -> 3200ms, capped to MaxDelay=1000ms
            Assert.Equal(1000, InvokeCalculateDelay(policy, 6).TotalMilliseconds);
        }

        // ---------------------------------------------------------------
        // MaxRetries=0 edge case
        // ---------------------------------------------------------------

        [Fact]
        public async Task ExecuteAsync_MaxRetriesZero_NeverInvokesOperation_ThrowsRetryExhaustedImmediately()
        {
            // Characterization test: the for-loop is `for (attempt = 0; attempt < MaxRetries; attempt++)`.
            // With MaxRetries=0 the loop body NEVER runs, so the operation is not invoked even
            // once -- callers cannot use MaxRetries=0 to mean "try once, no retries"; it means
            // "never try at all". This is surprising but is the actual current behavior.
            var options = new RetryPolicyOptions { MaxRetries = 0 };
            var policy = new ExponentialBackoffRetryPolicy(NullLogger.Instance, options);
            var invocations = 0;

            var ex = await Assert.ThrowsAsync<RetryExhaustedException>(async () =>
                await policy.ExecuteAsync(_ =>
                {
                    Interlocked.Increment(ref invocations);
                    return Task.FromResult(42);
                }, "zero-retry-op", CancellationToken.None));

            Assert.Equal(0, invocations);
            Assert.Null(ex.InnerException);
            Assert.Contains("0 attempts", ex.Message);
        }

        // ---------------------------------------------------------------
        // Retry-vs-give-up boundary
        // ---------------------------------------------------------------

        [Fact]
        public async Task ExecuteAsync_AllAttemptsFail_ThrowsRetryExhaustedAfterExactlyMaxRetriesAttempts()
        {
            var options = new RetryPolicyOptions
            {
                MaxRetries = 3,
                InitialDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(5),
                UseJitter = true
            };
            var policy = new ExponentialBackoffRetryPolicy(NullLogger.Instance, options);
            var invocations = 0;

            var ex = await Assert.ThrowsAsync<RetryExhaustedException>(async () =>
                await policy.ExecuteAsync<int>(_ =>
                {
                    Interlocked.Increment(ref invocations);
                    throw new TransientException("boom");
                }, "always-fails", CancellationToken.None));

            Assert.Equal(3, invocations);
            Assert.IsType<TransientException>(ex.InnerException);
            Assert.Contains("3 attempts", ex.Message);
        }

        [Fact]
        public async Task ExecuteAsync_SucceedsOnFinalAttempt_ReturnsResult()
        {
            var options = new RetryPolicyOptions
            {
                MaxRetries = 3,
                InitialDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(5),
                UseJitter = true
            };
            var policy = new ExponentialBackoffRetryPolicy(NullLogger.Instance, options);
            var invocations = 0;

            var result = await policy.ExecuteAsync(_ =>
            {
                var attemptNumber = Interlocked.Increment(ref invocations);
                if (attemptNumber < 3)
                {
                    throw new TransientException("not yet");
                }

                return Task.FromResult(99);
            }, "succeeds-eventually", CancellationToken.None);

            Assert.Equal(99, result);
            Assert.Equal(3, invocations);
        }

        // ---------------------------------------------------------------
        // Cancellation honored mid-backoff
        // ---------------------------------------------------------------

        [Fact]
        public async Task ExecuteAsync_CancellationDuringBackoffDelay_ThrowsOperationCanceled_NotRetryExhausted()
        {
            var options = new RetryPolicyOptions
            {
                MaxRetries = 5,
                InitialDelay = TimeSpan.FromMilliseconds(500),
                MaxDelay = TimeSpan.FromSeconds(5),
                UseJitter = false // deterministic 500ms delay before attempt 2
            };
            var policy = new ExponentialBackoffRetryPolicy(NullLogger.Instance, options);
            var invocations = 0;
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));

            var sw = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await policy.ExecuteAsync<int>(ct =>
                {
                    Interlocked.Increment(ref invocations);
                    throw new TransientException("keeps failing");
                }, "cancel-mid-backoff", cts.Token));
            sw.Stop();

            // Only the first attempt should have run; cancellation should fire during the
            // 500ms backoff delay before a second attempt, well short of 500ms elapsed.
            Assert.Equal(1, invocations);
            Assert.True(sw.ElapsedMilliseconds < 450, $"expected cancellation well before the 500ms backoff completed, took {sw.ElapsedMilliseconds}ms");
        }

        [Fact]
        public async Task ExecuteAsync_PreCancelledToken_ThrowsWithoutInvokingOperation()
        {
            var policy = new ExponentialBackoffRetryPolicy(NullLogger.Instance, RetryPolicyOptions.Default);
            var invocations = 0;
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await policy.ExecuteAsync<int>(_ =>
                {
                    Interlocked.Increment(ref invocations);
                    return Task.FromResult(1);
                }, "pre-cancelled", cts.Token));

            Assert.Equal(0, invocations);
        }

        // ---------------------------------------------------------------
        // Custom retry-predicate interaction
        // ---------------------------------------------------------------

        [Fact]
        public async Task ExecuteAsync_ShouldRetryReturnsFalse_ThrowsOriginalException_NotRetryExhausted()
        {
            var options = new RetryPolicyOptions
            {
                MaxRetries = 3,
                InitialDelay = TimeSpan.FromMilliseconds(1),
                ShouldRetry = ex => ex is TransientException // only TransientException is retried
            };
            var policy = new ExponentialBackoffRetryPolicy(NullLogger.Instance, options);
            var invocations = 0;

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await policy.ExecuteAsync<int>(_ =>
                {
                    Interlocked.Increment(ref invocations);
                    throw new InvalidOperationException("non-transient, permanent failure");
                }, "non-retryable", CancellationToken.None));

            Assert.Equal("non-transient, permanent failure", thrown.Message);
            Assert.Equal(1, invocations); // gave up after the first attempt, no retries
        }

        [Fact]
        public async Task ExecuteAsync_ShouldRetryReturnsTrue_RetriesTransientException_UntilSuccess()
        {
            var options = new RetryPolicyOptions
            {
                MaxRetries = 4,
                InitialDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(5),
                ShouldRetry = ex => ex is TransientException
            };
            var policy = new ExponentialBackoffRetryPolicy(NullLogger.Instance, options);
            var invocations = 0;

            var result = await policy.ExecuteAsync(_ =>
            {
                var attemptNumber = Interlocked.Increment(ref invocations);
                if (attemptNumber < 3)
                {
                    throw new TransientException();
                }

                return Task.FromResult("ok");
            }, "transient-then-ok", CancellationToken.None);

            Assert.Equal("ok", result);
            Assert.Equal(3, invocations);
        }

        // ---------------------------------------------------------------
        // Overload sanity (non-CancellationToken overloads delegate correctly)
        // ---------------------------------------------------------------

        [Fact]
        public async Task ExecuteAsync_FuncTaskOfT_Overload_DelegatesToCancellationTokenOverload()
        {
            var policy = new ExponentialBackoffRetryPolicy(NullLogger.Instance, RetryPolicyOptions.Default);
            var result = await policy.ExecuteAsync(() => Task.FromResult(7), "no-ct-overload");
            Assert.Equal(7, result);
        }

        [Fact]
        public async Task ExecuteAsync_VoidOverload_RunsOperationToCompletion()
        {
            var policy = new ExponentialBackoffRetryPolicy(NullLogger.Instance, RetryPolicyOptions.Default);
            var ran = false;
            await policy.ExecuteAsync(() =>
            {
                ran = true;
                return Task.CompletedTask;
            }, "void-overload");

            Assert.True(ran);
        }

        [Fact]
        public async Task ExecuteAsync_NullOperation_ThrowsArgumentNullException()
        {
            var policy = new ExponentialBackoffRetryPolicy(NullLogger.Instance, RetryPolicyOptions.Default);
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await policy.ExecuteAsync<int>((Func<CancellationToken, Task<int>>)null!, "null-op", CancellationToken.None));
        }
    }
}
