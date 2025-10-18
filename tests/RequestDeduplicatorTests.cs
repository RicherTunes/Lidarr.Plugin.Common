using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Deduplication;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class RequestDeduplicatorTests
    {
        [Fact]
        public async Task Dispose_CancelsPendingRequests()
        {
            var dedupe = new RequestDeduplicator(new NullLogger<RequestDeduplicator>(),
                requestTimeout: TimeSpan.FromMilliseconds(200), cleanupInterval: TimeSpan.FromMilliseconds(100));

            var startedTcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockedTcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

            var task = dedupe.GetOrCreateAsync("long-running", async () =>
            {
                startedTcs.SetResult(null);
                await blockedTcs.Task;
                return "done";
            });

            await startedTcs.Task; // ensure factory is executing
            dedupe.Dispose();
            blockedTcs.SetResult(null);

            await Assert.ThrowsAsync<TaskCanceledException>(() => task);
        }

        [Fact]
        public async Task CoalescedRequestHonorsCancellationAndFallsBack()
        {
            var dedupe = new RequestDeduplicator(new NullLogger<RequestDeduplicator>(),
                requestTimeout: TimeSpan.FromSeconds(5), cleanupInterval: TimeSpan.FromSeconds(5));

            var firstStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstRelease = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

            var primaryTask = dedupe.GetOrCreateAsync("shared-key", async () =>
            {
                firstStarted.TrySetResult(null);
                await firstRelease.Task;
                return "primary";
            });

            await firstStarted.Task; // ensure the primary request is in flight

            var fallbackInvocations = 0;
            using var shortTimeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            var stopwatch = Stopwatch.StartNew();

            var fallbackResult = await dedupe.GetOrCreateAsync("shared-key", () =>
            {
                Interlocked.Increment(ref fallbackInvocations);
                return Task.FromResult("fallback");
            }, shortTimeoutCts.Token);

            stopwatch.Stop();

            Assert.Equal("fallback", fallbackResult);
            Assert.Equal(1, fallbackInvocations);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), "Fallback should occur before the timeout window elapses.");

            firstRelease.SetResult(null);
            Assert.Equal("primary", await primaryTask);

            dedupe.Dispose();
        }
    }
}
