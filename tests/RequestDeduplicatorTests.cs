using System;
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
    }
}
