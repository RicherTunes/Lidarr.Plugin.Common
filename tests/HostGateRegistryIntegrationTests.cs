using System;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class HostGateRegistryIntegrationTests
    {
        private static async Task RunBatchAsync(string host, int requests, TimeSpan work, int maxConcurrency)
        {
            var current = 0;
            var observedMax = 0;

            Func<string, CancellationToken, Task<int>> send = async (_, ct) =>
            {
                var now = Interlocked.Increment(ref current);
                Volatile.Write(ref observedMax, Math.Max(observedMax, now));
                try { await Task.Delay(work, ct).ConfigureAwait(false); }
                finally { Interlocked.Decrement(ref current); }
                return 200;
            };

            Func<string, Task<string>> clone = s => Task.FromResult(s);

            var policy = ResiliencePolicy.Default.With(maxRetries: 1, retryBudget: TimeSpan.FromSeconds(1), maxConcurrencyPerHost: maxConcurrency, perRequestTimeout: null);

            var tasks = new Task<int>[requests];
            for (var i = 0; i < requests; i++)
            {
                tasks[i] = GenericResilienceExecutor.ExecuteWithResilienceAsync(
                    host,
                    send,
                    clone,
                    h => h,                  // host selector uses the string itself
                    status => status,        // pass through
                    _ => null,               // no Retry-After
                    policy,
                    CancellationToken.None);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            Assert.InRange(Volatile.Read(ref observedMax), 1, maxConcurrency);
        }

        [Fact]
        public async Task Shutdown_During_Load_Does_Not_Throw_And_Gates_Recreate()
        {
            var host = "host-gate.shutdown.test";

            // Warm-up: ensure a gate exists
            await RunBatchAsync(host, requests: 2, work: TimeSpan.FromMilliseconds(10), maxConcurrency: 2);

            // Launch a batch and call Shutdown mid-flight
            var batch1 = RunBatchAsync(host, requests: 10, work: TimeSpan.FromMilliseconds(30), maxConcurrency: 2);
            await Task.Delay(15);
            HostGateRegistry.Shutdown();
            await batch1; // should complete without ObjectDisposedException

            // Subsequent requests should recreate gates and honor concurrency limit
            await RunBatchAsync(host, requests: 6, work: TimeSpan.FromMilliseconds(20), maxConcurrency: 2);
        }

        [Fact]
        public async Task Clear_During_Load_Does_Not_Throw_And_Subsequent_Requests_Work()
        {
            var host = "host-gate.clear.test";

            var batch1 = RunBatchAsync(host, requests: 8, work: TimeSpan.FromMilliseconds(25), maxConcurrency: 2);
            await Task.Delay(10);
            HostGateRegistry.Clear(host);
            await batch1;

            await RunBatchAsync(host, requests: 5, work: TimeSpan.FromMilliseconds(20), maxConcurrency: 2);
        }
    }
}
