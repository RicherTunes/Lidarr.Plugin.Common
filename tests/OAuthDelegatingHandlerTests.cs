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
    }
}
