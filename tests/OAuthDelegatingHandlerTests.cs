using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class OAuthDelegatingHandlerTests
    {
        private sealed class TestTokenProvider : IStreamingTokenProvider
        {
            private string _token = "initial";
            private readonly TimeSpan _refreshDelay;
            private int _refreshCount;

            public TestTokenProvider(TimeSpan refreshDelay)
            {
                _refreshDelay = refreshDelay;
            }

            public int RefreshCount => Volatile.Read(ref _refreshCount);

            public Task<string> GetAccessTokenAsync() => Task.FromResult(_token);

            public async Task<string> RefreshTokenAsync()
            {
                await Task.Delay(_refreshDelay);
                var count = Interlocked.Increment(ref _refreshCount);
                _token = $"refreshed-{count}";
                return _token;
            }

            public Task<bool> ValidateTokenAsync(string token) => Task.FromResult(true);

            public DateTime? GetTokenExpiration(string token) => null;

            public void ClearAuthenticationCache() { }

            public bool SupportsRefresh => true;

            public string ServiceName => "Test";
        }

        private sealed class AuthorizationEchoHandler : HttpMessageHandler
        {
            private int _callCount;

            public int CallCount => Volatile.Read(ref _callCount);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _callCount);

                var token = request.Headers.Authorization?.Parameter;
                if (token != null && token.StartsWith("refreshed-", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        RequestMessage = request,
                        Content = new StringContent("ok")
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    RequestMessage = request
                });
            }
        }

        [Fact]
        public async Task Concurrent401s_TriggerSingleRefresh()
        {
            var provider = new TestTokenProvider(TimeSpan.FromMilliseconds(50));
            var inner = new AuthorizationEchoHandler();
            var oauth = new OAuthDelegatingHandler(provider, NullLogger.Instance)
            {
                InnerHandler = inner
            };

            using var client = new HttpClient(oauth, disposeHandler: true);

            var first = client.GetAsync("https://api.test/resource1");
            var second = client.GetAsync("https://api.test/resource2");

            var responses = await Task.WhenAll(first, second);

            foreach (var response in responses)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                response.Dispose();
            }

            Assert.Equal(1, provider.RefreshCount);
            Assert.Equal(4, inner.CallCount);
        }

        // A request with a ONE-SHOT body (e.g. StreamContent / any content whose stream is consumed by the
        // first send) must survive a 401-refresh-retry: the body has to be replayed on the retry. Before the
        // fix, the first send consumed the content and CloneForRetryAsync then read an exhausted stream —
        // turning a recoverable 401 into a hard failure (or an empty-bodied retry).
        [Fact]
        public async Task Post_WithOneShotBody_On401Retry_ReplaysTheBody()
        {
            var provider = new TestTokenProvider(TimeSpan.Zero);
            var inner = new CapturingRetryHandler();
            var oauth = new OAuthDelegatingHandler(provider, NullLogger.Instance)
            {
                InnerHandler = inner
            };
            using var client = new HttpClient(oauth, disposeHandler: true);

            var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.test/upload")
            {
                Content = new OneShotContent(payload)
            };

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, provider.RefreshCount);
            Assert.Equal(2, inner.Calls); // 401 then 200
            Assert.NotNull(inner.BodyOnSuccess);
            Assert.Equal(payload, inner.BodyOnSuccess); // the body was replayed on the retry, not lost
        }

        /// <summary>A body whose stream can be serialized exactly ONCE (mimics StreamContent over a network
        /// stream): a second serialize/read throws, exposing any path that fails to buffer for retry.</summary>
        private sealed class OneShotContent : HttpContent
        {
            private readonly byte[] _bytes;
            private bool _consumed;

            public OneShotContent(byte[] bytes)
            {
                _bytes = bytes;
                Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            }

            protected override async Task SerializeToStreamAsync(System.IO.Stream stream, System.Net.TransportContext? context)
            {
                if (_consumed)
                {
                    throw new InvalidOperationException("The request stream was already consumed; it cannot be sent again.");
                }

                _consumed = true;
                await stream.WriteAsync(_bytes, 0, _bytes.Length).ConfigureAwait(false);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = _bytes.Length;
                return true;
            }
        }

        /// <summary>Returns 401 until the Authorization is a refreshed token, then captures the body it received
        /// on the successful (retry) attempt so the test can assert the body was replayed.</summary>
        private sealed class CapturingRetryHandler : HttpMessageHandler
        {
            private int _calls;
            public int Calls => Volatile.Read(ref _calls);
            public byte[]? BodyOnSuccess;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _calls);

                // Simulate the server consuming the request body the way a real send does — via
                // SerializeToStreamAsync (CopyToAsync), NOT ReadAsByteArrayAsync (which internally BUFFERS the
                // content and would hide a one-shot body being consumed). This is what makes the double faithful.
                var body = Array.Empty<byte>();
                if (request.Content != null)
                {
                    using var sink = new System.IO.MemoryStream();
                    await request.Content.CopyToAsync(sink, cancellationToken).ConfigureAwait(false);
                    body = sink.ToArray();
                }

                var token = request.Headers.Authorization?.Parameter;
                if (token == null || !token.StartsWith("refreshed-", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request };
                }

                BodyOnSuccess = body;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent("ok")
                };
            }
        }
    }
}
