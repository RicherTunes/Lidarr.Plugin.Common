using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
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
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

            public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
            {
                if (handler == null) throw new ArgumentNullException(nameof(handler));
                _handler = (req, _) => handler(req);
            }

            public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            {
                _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return _handler(request, cancellationToken);
            }
        }

        private sealed class ProblemDocument
        {
            public string Title { get; set; } = string.Empty;
        }

        // snippet:guard-stream
        // snippet-skip-compile
        private sealed class GuardStream : Stream
        {
            private readonly byte[] _data;
            private int _position;
            private bool _allowExtended;

            public GuardStream(byte[] data)
            {
                _data = data ?? Array.Empty<byte>();
            }

            public int FirstReadCount { get; private set; }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _data.Length;

            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                if (offset < 0 || count < 0 || buffer.Length - offset < count) throw new ArgumentOutOfRangeException();

                if (!_allowExtended)
                {
                    FirstReadCount = count;
                    if (count > 2)
                    {
                        throw new InvalidOperationException($"Expected sniffer to read at most 2 bytes but requested {count}.");
                    }

                    _allowExtended = true;
                }

                var remaining = _data.Length - _position;
                if (remaining <= 0)
                {
                    return 0;
                }

                var toCopy = Math.Min(count, remaining);
                Array.Copy(_data, _position, buffer, offset, toCopy);
                _position += toCopy;
                return toCopy;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return Task.FromResult(Read(buffer, offset, count));
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
        // end-snippet


        private static (SemaphoreSlim Semaphore, int Limit) GetHostGateState(string host)
        {
            if (!HostGateRegistry.TryGetState(host, out var state))
            {
                throw new InvalidOperationException($"Host gate for '{host}' not found.");
            }

            return state;
        }

        private static void ClearHostGate(string host)
        {
            HostGateRegistry.Clear(host);
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
                maxConcurrencyPerHost: 1,
                perRequestTimeout: null,
                cancellationToken: CancellationToken.None))
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
                    maxConcurrencyPerHost: 4,
                    perRequestTimeout: null,
                    cancellationToken: CancellationToken.None))
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
        public async Task ExecuteWithResilienceAsync_ThrowsTimeoutExceptionWhenPerRequestTimeoutExceeded()
        {
            var handler = new StubHandler(async (req, ct) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://timeout.test/data");

            await Assert.ThrowsAsync<TimeoutException>(() => client.ExecuteWithResilienceAsync(
                request,
                maxRetries: 1,
                retryBudget: TimeSpan.FromSeconds(1),
                maxConcurrencyPerHost: 1,
                perRequestTimeout: TimeSpan.FromMilliseconds(50),
                cancellationToken: CancellationToken.None));
        }

        [Fact]
        public async Task ExecuteWithResilienceAsync_UsesProfileInHostGateKey_WhenProvided()
        {
            var host = $"profile-{Guid.NewGuid():N}.example";
            var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
            using var client = new HttpClient(handler);

            var searchBuilder = new StreamingApiRequestBuilder($"https://{host}")
                .Endpoint("one")
                .WithPolicy(ResiliencePolicy.Search);

            using (await client.SendWithResilienceAsync(searchBuilder, maxRetries: 0, retryBudget: TimeSpan.FromSeconds(1), maxConcurrencyPerHost: 2))
            { }

            // The gate should be keyed by host|profile
            var compositeKey = host + "|" + ResiliencePolicy.Search.Name;
            var state = GetHostGateState(compositeKey);
            try
            {
                Assert.Equal(2, state.Limit);
            }
            finally
            {
                ClearHostGate(compositeKey);
            }
        }

        [Fact]
        public async Task SendWithResilienceAsync_HonorsBuilderPolicy_WhenPresent()
        {
            var handler = new StubHandler(async (req, ct) =>
            {
                // Simulate slow server to trigger per-request timeout from policy
                await Task.Delay(TimeSpan.FromMilliseconds(120), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

            using var client = new HttpClient(handler);
            var builder = new StreamingApiRequestBuilder("https://example.policy")
                .Endpoint("timeout/test")
                .WithPolicy(ResiliencePolicy.Default.With(perRequestTimeout: TimeSpan.FromMilliseconds(50)));

            await Assert.ThrowsAsync<TimeoutException>(() => client.SendWithResilienceAsync(
                builder,
                maxRetries: 1,
                retryBudget: TimeSpan.FromSeconds(1),
                maxConcurrencyPerHost: 1,
                cancellationToken: CancellationToken.None));
        }
        // snippet:resilience-cancel
        // snippet-skip-compile
        [Fact]
        public async Task ExecuteWithResilienceAsync_RespectsCallerCancellation()
        {
            var host = $"cancel-{Guid.NewGuid():N}.example";
            var handler = new StubHandler(async (req, ct) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/resource");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExecuteWithResilienceAsync(
                request,
                maxRetries: 1,
                retryBudget: TimeSpan.FromSeconds(1),
                maxConcurrencyPerHost: 2,
                perRequestTimeout: TimeSpan.FromSeconds(5),
                cancellationToken: cts.Token));

            ClearHostGate(host);
        }
        // end-snippet


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
        public async Task ContentDecodingSniffer_PeeksOnlyHeaderBytesForPlainContent()
        {
            var payloadText = @"{""guard"":true}";
            var payloadBytes = Encoding.UTF8.GetBytes(payloadText);
            var guardStream = new GuardStream(payloadBytes);

            var handler = new ContentDecodingSnifferHandler
            {
                InnerHandler = new StubHandler((_, __) =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(guardStream)
                    };
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    response.Content.Headers.ContentLength = payloadBytes.Length;
                    return Task.FromResult(response);
                })
            };

            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://example.com/plain");

            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(payloadText, body);
            Assert.Equal(2, guardStream.FirstReadCount);
        }
        // end-snippet
        [Fact]
        public async Task ContentDecodingSniffer_StripsEncodingHeadersAfterInflate()
        {
            await using var compressedStream = new MemoryStream();
            await using (var gzip = new GZipStream(compressedStream, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                var payloadBytes = Encoding.UTF8.GetBytes("{\"name\":\"sniffer\"}");
                await gzip.WriteAsync(payloadBytes);
            }
            var compressedLength = compressedStream.Length;
            compressedStream.Position = 0;

            var handler = new ContentDecodingSnifferHandler
            {
                InnerHandler = new StubHandler(_ =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(compressedStream)
                    };
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                    response.Content.Headers.ContentLength = compressedStream.Length;
                    return Task.FromResult(response);
                })
            };

            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://example.com/sniff");

            var response = await client.SendAsync(request);
            var payload = await response.Content.ReadAsStringAsync();

            Assert.Empty(response.Content.Headers.ContentEncoding);
            Assert.NotEqual(compressedLength, response.Content.Headers.ContentLength);
            Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("{\"name\":\"sniffer\"}", payload);
        }
        // end-snippet

        // snippet:sniffer-passthrough
        // snippet-skip-compile
        [Fact]
        public async Task ContentDecodingSniffer_PreservesContentLengthForPassthrough()
        {
            var payloadBytes = Encoding.UTF8.GetBytes("plain payload");
            var guardStream = new GuardStream(payloadBytes);

            var handler = new ContentDecodingSnifferHandler
            {
                InnerHandler = new StubHandler(_ =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(guardStream)
                    };
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                    response.Content.Headers.ContentLength = payloadBytes.Length;
                    return Task.FromResult(response);
                })
            };

            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://example.com/plain-length");

            var response = await client.SendAsync(request);
            var payload = await response.Content.ReadAsStringAsync();

            Assert.Equal(payloadBytes.Length, response.Content.Headers.ContentLength);
            Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("plain payload", payload);
            Assert.Equal(2, guardStream.FirstReadCount);
        }
        // end-snippet



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
