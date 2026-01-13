using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.TestKit.Testing;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class FakeTimeProviderTests
    {
        [Fact]
        public async Task Delay_CompletesOnlyAfterAdvance()
        {
            // Arrange: schedule 3 delays (1s, 2s, 3s)
            var fakeTime = new FakeTimeProvider();
            var completed = new ConcurrentBag<int>();

            var delay1 = Task.Run(async () =>
            {
                await fakeTime.CreateDelayTask(TimeSpan.FromSeconds(1));
                completed.Add(1);
            });

            var delay2 = Task.Run(async () =>
            {
                await fakeTime.CreateDelayTask(TimeSpan.FromSeconds(2));
                completed.Add(2);
            });

            var delay3 = Task.Run(async () =>
            {
                await fakeTime.CreateDelayTask(TimeSpan.FromSeconds(3));
                completed.Add(3);
            });

            // Give tasks time to register their delays
            await Task.Delay(50);

            // Act: advance 2 seconds - first two should complete
            fakeTime.Advance(TimeSpan.FromSeconds(2));

            // Allow completions to propagate
            await Task.Delay(50);

            // Assert: exactly first two complete
            Assert.Contains(1, completed);
            Assert.Contains(2, completed);
            Assert.DoesNotContain(3, completed);

            // Cleanup: complete the third
            fakeTime.Advance(TimeSpan.FromSeconds(1));
            await Task.WhenAll(delay1, delay2, delay3).WaitAsync(TimeSpan.FromSeconds(1));
        }

        [Fact]
        public async Task Delay_AdvancePastDeadline_CompletesImmediately()
        {
            // Arrange
            var fakeTime = new FakeTimeProvider();
            var completed = false;

            var delayTask = Task.Run(async () =>
            {
                await fakeTime.CreateDelayTask(TimeSpan.FromSeconds(5));
                completed = true;
            });

            await Task.Delay(50); // Let task register

            // Act: advance big jump past deadline
            fakeTime.Advance(TimeSpan.FromSeconds(100));

            // Assert: completes immediately
            await delayTask.WaitAsync(TimeSpan.FromMilliseconds(100));
            Assert.True(completed);
        }

        [Fact]
        public async Task Delay_Cancellation_CancelsPromptly()
        {
            // Arrange
            var fakeTime = new FakeTimeProvider();
            using var cts = new CancellationTokenSource();

            var delayTask = fakeTime.CreateDelayTask(TimeSpan.FromSeconds(60), cts.Token);

            // Act: cancel without advancing time
            cts.Cancel();

            // Assert: must complete as canceled without needing Advance
            var ex = await Assert.ThrowsAsync<TaskCanceledException>(() =>
                delayTask.WaitAsync(TimeSpan.FromMilliseconds(100)));
            Assert.True(delayTask.IsCanceled);
        }

        [Fact]
        public async Task Delay_RegisterAfterAdvance_StillBehaves()
        {
            // Arrange: advance time first
            var fakeTime = new FakeTimeProvider();
            fakeTime.Advance(TimeSpan.FromSeconds(100));

            // Now schedule a short delay
            var completed = false;
            var delayTask = Task.Run(async () =>
            {
                await fakeTime.CreateDelayTask(TimeSpan.FromSeconds(1));
                completed = true;
            });

            await Task.Delay(50); // Let task register

            // Assert: should NOT complete immediately (not stale state)
            Assert.False(completed);

            // Act: advance more time
            fakeTime.Advance(TimeSpan.FromSeconds(1));

            // Assert: now completes
            await delayTask.WaitAsync(TimeSpan.FromMilliseconds(100));
            Assert.True(completed);
        }

        [Fact]
        public async Task Timer_FiresDeterministically()
        {
            // Arrange
            var fakeTime = new FakeTimeProvider();
            var callbackCount = 0;

            using var timer = fakeTime.CreateTimer(
                _ => Interlocked.Increment(ref callbackCount),
                null,
                TimeSpan.FromSeconds(1),
                Timeout.InfiniteTimeSpan);

            // Act: advance time to fire timer
            fakeTime.Advance(TimeSpan.FromSeconds(1));

            // Allow callback to execute
            await Task.Delay(50);

            // Assert: callback fired exactly once
            Assert.Equal(1, callbackCount);

            // Dispose should stop future fires
            timer.Dispose();

            // Advance more - should not fire again
            fakeTime.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(50);

            Assert.Equal(1, callbackCount);
        }

        [Fact]
        public async Task ThreadSafety_ConcurrentDelayRegistration()
        {
            // Arrange
            var fakeTime = new FakeTimeProvider();
            const int taskCount = 100;
            var completedCount = 0;
            var tasks = new Task[taskCount];

            // Act: in parallel register N delays
            for (int i = 0; i < taskCount; i++)
            {
                var delay = TimeSpan.FromMilliseconds((i % 10 + 1) * 100); // 100ms to 1000ms
                tasks[i] = Task.Run(async () =>
                {
                    await fakeTime.CreateDelayTask(delay);
                    Interlocked.Increment(ref completedCount);
                });
            }

            // Give tasks time to register
            await Task.Delay(100);

            // Advance time in chunks to test concurrent completion
            for (int i = 0; i < 10; i++)
            {
                fakeTime.Advance(TimeSpan.FromMilliseconds(100));
                await Task.Yield();
            }

            // Assert: must not deadlock and must complete expected count
            var allCompleted = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5))
                .ContinueWith(t => !t.IsFaulted && !t.IsCanceled);

            Assert.True(allCompleted, "Tasks should complete without deadlock");
            Assert.Equal(taskCount, completedCount);
        }

        [Fact]
        public void NoRealTimeDependency_GetUtcNow_DoesNotUseWallClock()
        {
            // Arrange
            var startTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var fakeTime = new FakeTimeProvider(startTime);

            // Act: get time multiple times quickly
            var t1 = fakeTime.GetUtcNow();
            var t2 = fakeTime.GetUtcNow();
            var t3 = fakeTime.GetUtcNow();

            // Assert: all times are exactly the same (no wall clock influence)
            Assert.Equal(startTime, t1);
            Assert.Equal(startTime, t2);
            Assert.Equal(startTime, t3);

            // Only Advance changes time
            fakeTime.Advance(TimeSpan.FromHours(1));
            var t4 = fakeTime.GetUtcNow();

            Assert.Equal(startTime.AddHours(1), t4);
        }

        [Fact]
        public void Advance_NegativeTimeSpan_Throws()
        {
            var fakeTime = new FakeTimeProvider();

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                fakeTime.Advance(TimeSpan.FromSeconds(-1)));
        }

        [Fact]
        public void SetUtcNow_BackwardsTime_Throws()
        {
            var fakeTime = new FakeTimeProvider();
            fakeTime.Advance(TimeSpan.FromHours(1));

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                fakeTime.SetUtcNow(fakeTime.GetUtcNow().AddSeconds(-1)));
        }

        [Fact]
        public async Task Delay_ZeroOrNegative_CompletesImmediately()
        {
            var fakeTime = new FakeTimeProvider();

            var zeroDelay = fakeTime.CreateDelayTask(TimeSpan.Zero);
            var negativeDelay = fakeTime.CreateDelayTask(TimeSpan.FromSeconds(-1));

            // Both should complete immediately without Advance
            Assert.True(zeroDelay.IsCompleted);
            Assert.True(negativeDelay.IsCompleted);

            await zeroDelay;
            await negativeDelay;
        }

        [Fact]
        public void PendingDelayCount_TracksCorrectly()
        {
            var fakeTime = new FakeTimeProvider();

            Assert.Equal(0, fakeTime.PendingDelayCount);

            // Start some delays (do not await them)
            var t1 = fakeTime.CreateDelayTask(TimeSpan.FromSeconds(1));
            var t2 = fakeTime.CreateDelayTask(TimeSpan.FromSeconds(2));
            var t3 = fakeTime.CreateDelayTask(TimeSpan.FromSeconds(3));

            Assert.Equal(3, fakeTime.PendingDelayCount);

            // Advance to complete first
            fakeTime.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(2, fakeTime.PendingDelayCount);

            // Complete all
            fakeTime.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(0, fakeTime.PendingDelayCount);
        }
    }
}
