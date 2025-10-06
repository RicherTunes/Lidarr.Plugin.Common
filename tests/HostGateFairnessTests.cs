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
        private sealed class NotifyingHandler : DelegatingHandler
        {
            private readonly ConcurrentQueue<string> _started = new();
            private readonly TimeSpan _work;
            public NotifyingHandler(TimeSpan work) { _work = work; }
            public string[] StartedProfiles => _started.ToArray();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var profile = request.Options.TryGetValue(PluginHttpOptions.ProfileKey, out string? p) ? p ?? string.Empty : string.Empty;
                _started.Enqueue(profile);
                await Task.Delay(_work, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        }

        [Fact]
        public async Task AggregateHostGate_Allows_Progress_For_All_Profiles()
        {
            var handler = new NotifyingHandler(TimeSpan.FromMilliseconds(50));
            using var client = new HttpClient(handler);

            var bA = new StreamingApiRequestBuilder("https://fair.example").Endpoint("res").WithPolicy(ResiliencePolicy.Default.With(name: "A", maxConcurrencyPerHost: 2));
            var bB = new StreamingApiRequestBuilder("https://fair.example").Endpoint("res").WithPolicy(ResiliencePolicy.Default.With(name: "B", maxConcurrencyPerHost: 2));
            var bC = new StreamingApiRequestBuilder("https://fair.example").Endpoint("res").WithPolicy(ResiliencePolicy.Default.With(name: "C", maxConcurrencyPerHost: 2));

            // Launch one request per profile concurrently; aggregate cap=2 means 2 start, 1 waits.
            var t1 = client.SendWithResilienceAsync(bA, maxRetries: 0, retryBudget: TimeSpan.FromSeconds(1), maxConcurrencyPerHost: 2);
            var t2 = client.SendWithResilienceAsync(bB, maxRetries: 0, retryBudget: TimeSpan.FromSeconds(1), maxConcurrencyPerHost: 2);
            var t3 = client.SendWithResilienceAsync(bC, maxRetries: 0, retryBudget: TimeSpan.FromSeconds(1), maxConcurrencyPerHost: 2);
            await Task.WhenAll(t1, t2, t3);

            var started = handler.StartedProfiles;
            Assert.Equal(3, started.Length);
            Assert.Contains("A", started);
            Assert.Contains("B", started);
            Assert.Contains("C", started);
        }
    }
}

