using System;
using System.IO;
using System.Net;
using System.Net.Http;
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
