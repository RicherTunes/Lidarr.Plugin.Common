using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class HostGateFairnessTests
    {
        private sealed class GatedHandler : DelegatingHandler
        {
            private readonly ConcurrentQueue<string> _started = new();
            private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _gates = new();
            private int _requestId;
            
            public string[] StartedProfiles => _started.ToArray();
            public int StartedCount => _started.Count;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var profile = request.Options.TryGetValue(PluginHttpOptions.ProfileKey, out string? p) ? p ?? string.Empty : string.Empty;
                _started.Enqueue(profile);
                
                var id = Interlocked.Increment(ref _requestId);
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _gates[id] = gate;
                
                // Wait for the gate to be released
                using var reg = cancellationToken.Register(() => gate.TrySetCanceled(cancellationToken));
                await gate.Task;
                
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            public void ReleaseAll()
            {
                foreach (var gate in _gates.Values)
                {
                    gate.TrySetResult(true);
                }
            }
        }

        [Fact]
        public async Task AggregateHostGate_Allows_Progress_For_All_Profiles()
        {
            var handler = new GatedHandler();
            using var client = new HttpClient(handler);

            var bA = new StreamingApiRequestBuilder("https://fair.example").Endpoint("res").WithPolicy(ResiliencePolicy.Default.With(name: "A", maxConcurrencyPerHost: 2));
            var bB = new StreamingApiRequestBuilder("https://fair.example").Endpoint("res").WithPolicy(ResiliencePolicy.Default.With(name: "B", maxConcurrencyPerHost: 2));
            var bC = new StreamingApiRequestBuilder("https://fair.example").Endpoint("res").WithPolicy(ResiliencePolicy.Default.With(name: "C", maxConcurrencyPerHost: 2));

            Task t1 = null!, t2 = null!, t3 = null!;
            try
            {
                // Launch one request per profile concurrently; aggregate cap=2 means 2 start, 1 waits.
                t1 = client.SendWithResilienceAsync(bA, maxRetries: 0, retryBudget: TimeSpan.FromSeconds(10), maxConcurrencyPerHost: 2);
                t2 = client.SendWithResilienceAsync(bB, maxRetries: 0, retryBudget: TimeSpan.FromSeconds(10), maxConcurrencyPerHost: 2);
                t3 = client.SendWithResilienceAsync(bC, maxRetries: 0, retryBudget: TimeSpan.FromSeconds(10), maxConcurrencyPerHost: 2);

                // Give the tasks a moment to start (they will block at the gate)
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (handler.StartedCount < 2 && DateTime.UtcNow < deadline)
                {
                    await Task.Yield();
                }
                
                // At least 2 requests should have started (cap=2)
                Assert.True(handler.StartedCount >= 2, $"Expected at least 2 started, got {handler.StartedCount}");
                
                // Release all gates so requests can complete
                handler.ReleaseAll();
                
                await Task.WhenAll(t1, t2, t3);

                var started = handler.StartedProfiles;
                Assert.Equal(3, started.Length);
                Assert.Contains("A", started);
                Assert.Contains("B", started);
                Assert.Contains("C", started);
            }
            finally
            {
                // Always release gates to prevent task leaks even if assertions fail
                handler.ReleaseAll();
                
                // Wait for any pending tasks to complete
                if (t1 != null || t2 != null || t3 != null)
                {
                    try
                    {
                        await Task.WhenAll(
                            t1 ?? Task.CompletedTask,
                            t2 ?? Task.CompletedTask,
                            t3 ?? Task.CompletedTask
                        ).WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch
                    {
                        // Ignore exceptions during cleanup
                    }
                }
            }
        }
    }
}
