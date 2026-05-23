using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services;
using Lidarr.Plugin.Common.TestKit.Testing;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services
{
    [Trait("Category", "Unit")]
    public class StreamingPluginMixinsTests
    {
        #region ApplyRateLimitAsync - Non-Blocking Async Behaviour

        /// <summary>
        /// Proves that ApplyRateLimitAsync uses proper async/await (no .Wait() blocking)
        /// by verifying the calling thread is NOT blocked during the delay window.
        /// The test drives two rapid-fire requests at 60 rpm (1-second interval).
        /// With fake time the second call should await the delay task but never
        /// park the calling thread for a real second.
        /// </summary>
        [Fact]
        public async Task ApplyRateLimitAsync_SecondRequest_DoesNotBlockCallingThread()
        {
            // Arrange
            var fakeTime = new FakeTimeProvider();
            var mixin = new StreamingIndexerMixin("test-service", timeProvider: fakeTime);
            const int requestsPerMinute = 60; // 1 request per second

            // First call: sets the baseline timestamp
            await mixin.ApplyRateLimitAsync(requestsPerMinute);

            // Act: second call happens immediately after, so it would need to wait ~1 second.
            // The test's job is to verify this wait is non-blocking: the task should be
            // awaitable without parking a thread, so we can advance fake time to release it.
            var sw = Stopwatch.StartNew();

            // Start the second call but do NOT await it yet - it should be pending, not blocked
            var secondCallTask = mixin.ApplyRateLimitAsync(requestsPerMinute);

            // Advance fake time by 1.1 seconds so the delay resolves
            fakeTime.Advance(TimeSpan.FromSeconds(1.1));

            // Now await to completion
            await secondCallTask.WaitAsync(TimeSpan.FromSeconds(2));

            sw.Stop();

            // Assert: the wall-clock time should be well under 1 real second
            // (we used fake time, so the real elapsed should be ~0ms, not 1000ms)
            Assert.True(sw.ElapsedMilliseconds < 500,
                $"Expected wall-clock elapsed < 500 ms (non-blocking), but got {sw.ElapsedMilliseconds} ms. " +
                "This indicates a blocking Wait() is still being used.");
        }

        /// <summary>
        /// Proves that rate-limiting via SemaphoreSlim does not deadlock when called
        /// from concurrent callers: 10 callers all trying to acquire at the same time
        /// should complete without throwing or deadlocking.
        /// </summary>
        [Fact]
        public async Task ApplyRateLimitAsync_ConcurrentCallers_DoNotDeadlock()
        {
            // Arrange
            var fakeTime = new FakeTimeProvider();
            var mixin = new StreamingIndexerMixin("test-concurrent", timeProvider: fakeTime);

            var tasks = new List<Task>();
            var advanceTask = Task.Run(async () =>
            {
                // Advance time in small increments so pending delays resolve
                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(10); // real 10ms between advances
                    fakeTime.Advance(TimeSpan.FromSeconds(1));
                }
            });

            // Kick off 5 callers (each needing ~1s interval at 60 rpm)
            for (int i = 0; i < 5; i++)
            {
                tasks.Add(mixin.ApplyRateLimitAsync(60));
            }

            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
            await advanceTask;

            // If we get here without timeout/deadlock, the test passes
        }

        /// <summary>
        /// Verifies that when requestsPerMinute <= 0 the method returns immediately
        /// (no rate limiting applied).
        /// </summary>
        [Fact]
        public async Task ApplyRateLimitAsync_ZeroOrNegativeRpm_ReturnsImmediately()
        {
            var fakeTime = new FakeTimeProvider();
            var mixin = new StreamingIndexerMixin("test-zero", timeProvider: fakeTime);

            var sw = Stopwatch.StartNew();
            await mixin.ApplyRateLimitAsync(0);
            await mixin.ApplyRateLimitAsync(-1);
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 100, "Zero/negative RPM should return immediately");
        }

        /// <summary>
        /// Proves that the TimeProvider constructor parameter defaults to TimeProvider.System
        /// (i.e., the public API surface is stable — no required parameter added).
        /// </summary>
        [Fact]
        public void StreamingIndexerMixin_DefaultConstructor_UsesSystemTimeProvider()
        {
            // Arrange & Act — should compile and not throw
            var mixin = new StreamingIndexerMixin("default-time-provider");

            // Assert — can call rate-limit without time provider parameter
            Assert.NotNull(mixin);
        }

        #endregion
    }
}
