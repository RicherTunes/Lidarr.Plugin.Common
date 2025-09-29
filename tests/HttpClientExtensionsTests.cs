using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class HttpClientExtensionsTests
    {
        private sealed class StubHandler : DelegatingHandler
        {
            private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

            public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return _handler(request);
            }
        }

        private sealed class ProblemDocument
        {
            public string Title { get; set; } = string.Empty;
        }

        private static FieldInfo GetHostGatesField() => typeof(HttpClientExtensions).GetField("_hostGates", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new InvalidOperationException("Host gate registry not found.");

        private static (int Limit, SemaphoreSlim Semaphore) GetHostGateState(string host)
        {
            var field = GetHostGatesField();
            var gates = field.GetValue(null) ?? throw new InvalidOperationException("Host gate dictionary unavailable.");
            var tryGetValue = field.FieldType.GetMethod("TryGetValue") ?? throw new InvalidOperationException("Unable to locate TryGetValue on host gate registry.");
            var args = new object?[] { host, null };
            var found = (bool)tryGetValue.Invoke(gates, args)!;
            if (!found || args[1] is null)
            {
                throw new InvalidOperationException($"Host gate for '{host}' not found.");
            }

            var gate = args[1]!;
            var gateType = gate.GetType();
            var limitProperty = gateType.GetProperty("Limit") ?? throw new InvalidOperationException("Host gate limit property missing.");
            var semaphoreProperty = gateType.GetProperty("Semaphore") ?? throw new InvalidOperationException("Host gate semaphore property missing.");
            var limit = (int)limitProperty.GetValue(gate)!;
            var semaphore = (SemaphoreSlim)semaphoreProperty.GetValue(gate)!;
            return (limit, semaphore);
        }

        private static void ClearHostGate(string host)
        {
            var field = GetHostGatesField();
            var gates = field.GetValue(null);
            if (gates == null)
            {
                return;
            }

            MethodInfo? tryRemove = null;
            foreach (var method in field.FieldType.GetMethods())
            {
                var parameters = method.GetParameters();
                if (method.Name == "TryRemove" && parameters.Length == 2 && parameters[0].ParameterType == typeof(string))
                {
                    tryRemove = method;
                    break;
                }
            }

            if (tryRemove == null)
            {
                return;
            }

            var args = new object?[] { host, null };
            var removed = (bool)tryRemove.Invoke(gates, args)!;
            if (removed && args[1] is not null)
            {
                var gate = args[1]!;
                var semaphoreProperty = gate.GetType().GetProperty("Semaphore") ?? throw new InvalidOperationException("Host gate semaphore property missing.");
                if (semaphoreProperty.GetValue(gate) is SemaphoreSlim semaphore)
                {
                    semaphore.Dispose();
                }
            }
        }

        [Fact]
        public async Task ExecuteWithResilienceAsync_UpgradesHostGateLimit()
        {
            var host = $"resize-{Guid.NewGuid():N}.example";
            var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
            using var client = new HttpClient(handler);

            using (await client.ExecuteWithResilienceAsync(
                new HttpRequestMessage(HttpMethod.Get, $"https://{host}/one"),
                maxRetries: 0,
                retryBudget: TimeSpan.FromSeconds(1),
                maxConcurrencyPerHost: 1))
            {
            }

            var initialState = GetHostGateState(host);
            var firstSemaphore = initialState.Semaphore;

            try
            {
                Assert.Equal(1, initialState.Limit);
                Assert.Equal(1, firstSemaphore.CurrentCount);

                using (await client.ExecuteWithResilienceAsync(
                    new HttpRequestMessage(HttpMethod.Get, $"https://{host}/two"),
                    maxRetries: 0,
                    retryBudget: TimeSpan.FromSeconds(1),
                    maxConcurrencyPerHost: 4))
                {
                }

                var upgradedState = GetHostGateState(host);

                Assert.Equal(4, upgradedState.Limit);
                Assert.Same(firstSemaphore, upgradedState.Semaphore);
                Assert.Equal(4, upgradedState.Semaphore.CurrentCount);
            }
            finally
            {
                ClearHostGate(host);
            }

        }

        [Fact]
        public async Task ContentDecodingSniffer_InflatesMislabelledGzip()
        {
            await using var compressedStream = new MemoryStream();
            await using (var gzip = new System.IO.Compression.GZipStream(compressedStream, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            {
                var payloadBytes = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");
                await gzip.WriteAsync(payloadBytes);
            }
            compressedStream.Position = 0;

            var handler = new ContentDecodingSnifferHandler
            {
                InnerHandler = new StubHandler(_ =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(compressedStream)
                    };
                    return Task.FromResult(response);
                })
            };

            var client = new HttpClient(handler);
            var request = new HttpRequestMessage(HttpMethod.Get, "http://example.com/test");

            var response = await client.SendAsync(request);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            var payload = await response.Content.ReadAsStringAsync();

            Assert.Equal("application/json", contentType);
            Assert.Equal("{\"hello\":\"world\"}", payload);
        }

        [Fact]
        public async Task GetJsonAsync_ThrowsWhenContentNotJson()
        {
            var handler = new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("not json", Encoding.UTF8, "text/plain")
                };
                return Task.FromResult(response);
            });

            var client = new HttpClient(handler);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HttpClientExtensions.GetJsonAsync<object>(client, "http://example.com/plain"));

            Assert.Contains("Expected JSON", ex.Message);
        }

        [Fact]
        public async Task GetJsonAsync_AllowsProblemJsonContentType()
        {
            var handler = new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"title\":\"Validation failed\"}", Encoding.UTF8, "application/problem+json")
                };
                return Task.FromResult(response);
            });

            var client = new HttpClient(handler);

            var result = await HttpClientExtensions.GetJsonAsync<ProblemDocument>(client, "http://example.com/problem");

            Assert.Equal("Validation failed", result.Title);
        }

        [Fact]
        public void IsJsonContent_TreatsProblemJsonAsJson()
        {
            using var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/problem+json")
            };

            Assert.True(HttpClientExtensions.IsJsonContent(response));
        }
    }
}
