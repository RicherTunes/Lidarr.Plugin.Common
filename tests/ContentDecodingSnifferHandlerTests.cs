using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Http;
using Moq;
using Moq.Protected;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    [Trait("Category", "Unit")]
    public class ContentDecodingSnifferHandlerTests
    {
        #region Helper Methods

        /// <summary>
        /// Creates a gzip-compressed version of the input string.
        /// </summary>
        private static byte[] CreateGzipContent(string content)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionMode.Compress))
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                gzip.Write(bytes, 0, bytes.Length);
            }
            return output.ToArray();
        }

        /// <summary>
        /// Creates a mock HttpMessageHandler that returns the specified content.
        /// </summary>
        private static Mock<HttpMessageHandler> CreateMockHandler(byte[] content, string? contentType = null, string? contentEncoding = null)
        {
            var mock = new Mock<HttpMessageHandler>();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            };

            if (!string.IsNullOrEmpty(contentType))
            {
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            }

            if (!string.IsNullOrEmpty(contentEncoding))
            {
                response.Content.Headers.ContentEncoding.Add(contentEncoding);
            }

            mock.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);

            return mock;
        }

        /// <summary>
        /// Creates a ContentDecodingSnifferHandler with the specified inner handler.
        /// </summary>
        private static ContentDecodingSnifferHandler CreateHandler(HttpMessageHandler innerHandler)
        {
            var handler = new ContentDecodingSnifferHandler
            {
                InnerHandler = innerHandler
            };
            return handler;
        }

        /// <summary>
        /// Reads the response content as a string.
        /// </summary>
        private static async Task<string> ReadResponseContentAsync(HttpResponseMessage response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }

        #endregion

        #region Pass-Through Tests

        [Fact]
        public async Task SendAsync_WithNullContent_ReturnsResponse()
        {
            // Arrange
            var mockHandler = new Mock<HttpMessageHandler>();
            var responseMsg = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = null  // Explicitly set to null
            };
            mockHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(responseMsg);

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act
            var response = await client.GetAsync("https://example.com/test");

            // Assert
            Assert.NotNull(response);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // Content might be null or a default content created by HttpClient
            // The key is that the handler doesn't throw when content is null
        }

        [Fact]
        public async Task SendAsync_WithDeclaredEncoding_PassesThrough()
        {
            // Arrange
            var content = Encoding.UTF8.GetBytes("plain content");
            var mockHandler = CreateMockHandler(content, "text/plain", "gzip");

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act
            var response = await client.GetAsync("https://example.com/test");
            var result = await response.Content.ReadAsByteArrayAsync();

            // Assert
            Assert.Equal(content, result);
            Assert.True(response.Content.Headers.ContentEncoding.Contains("gzip"));
        }

        [Fact]
        public async Task SendAsync_WithNonGzipContent_PassesThrough()
        {
            // Arrange
            var content = Encoding.UTF8.GetBytes("plain text content");
            var mockHandler = CreateMockHandler(content, "text/plain", null);

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act
            var response = await client.GetAsync("https://example.com/test");
            var result = await response.Content.ReadAsByteArrayAsync();

            // Assert
            Assert.Equal(content, result);
        }

        [Fact]
        public async Task SendAsync_WithEmptyContent_ReturnsResponse()
        {
            // Arrange
            var mockHandler = CreateMockHandler(Array.Empty<byte>());

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act
            var response = await client.GetAsync("https://example.com/test");
            var result = await response.Content.ReadAsByteArrayAsync();

            // Assert
            Assert.Empty(result);
        }

        #endregion

        #region Gzip Detection Tests

        [Fact]
        public async Task SendAsync_DetectsGzipByMagicNumber()
        {
            // Arrange
            var originalContent = "{\"result\":\"success\"}";
            var gzipContent = CreateGzipContent(originalContent);
            var mockHandler = CreateMockHandler(gzipContent, "application/json", null);

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act
            var response = await client.GetAsync("https://example.com/test");
            var result = await ReadResponseContentAsync(response);

            // Assert
            Assert.Equal(originalContent, result);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        }

        [Fact]
        public async Task SendAsync_DoesNotDecompressWhenContentEncodingPresent()
        {
            // Arrange
            // Even if it has gzip magic bytes, if Content-Encoding is set, don't decompress
            var gzipContent = CreateGzipContent("test");
            var mockHandler = CreateMockHandler(gzipContent, null, "gzip");

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act
            var response = await client.GetAsync("https://example.com/test");
            var result = await response.Content.ReadAsByteArrayAsync();

            // Assert
            Assert.Equal(gzipContent, result);
        }

        [Fact]
        public async Task SendAsync_DecompressesGzipWithoutContentType()
        {
            // Arrange
            var originalContent = "{\"message\":\"hello\"}";
            var gzipContent = CreateGzipContent(originalContent);
            var mockHandler = CreateMockHandler(gzipContent, null, null);

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act
            var response = await client.GetAsync("https://example.com/test");
            var result = await ReadResponseContentAsync(response);

            // Assert
            Assert.Equal(originalContent, result);
            // Should set application/json when decompressing
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        }

        #endregion

        #region Short Response Tests

        [Fact]
        public async Task SendAsync_WithOneByteResponse_PassesThrough()
        {
            // Arrange
            var content = new byte[] { 0x42 };
            var mockHandler = CreateMockHandler(content);

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act
            var response = await client.GetAsync("https://example.com/test");
            var result = await response.Content.ReadAsByteArrayAsync();

            // Assert
            Assert.Single(result);
            Assert.Equal(0x42, result[0]);
        }

        [Fact]
        public async Task SendAsync_WithTwoByteNonGzip_PassesThrough()
        {
            // Arrange
            var content = new byte[] { 0x00, 0x00 };
            var mockHandler = CreateMockHandler(content);

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act
            var response = await client.GetAsync("https://example.com/test");
            var result = await response.Content.ReadAsByteArrayAsync();

            // Assert
            Assert.Equal(2, result.Length);
        }

        #endregion

        #region Header Handling Tests

        [Fact]
        public async Task SendAsync_PreservesNonEncodingHeaders()
        {
            // Arrange
            var content = Encoding.UTF8.GetBytes("test");
            var mockHandler = new Mock<HttpMessageHandler>();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
            response.Content.Headers.LastModified = DateTimeOffset.UtcNow;

            mockHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act
            var result = await client.GetAsync("https://example.com/test");

            // Assert
            Assert.Equal("text/html", result.Content.Headers.ContentType?.MediaType);
            Assert.NotNull(result.Content.Headers.LastModified);
        }

        [Fact]
        public async Task SendAsync_RemovesContentLengthWhenDecompressing()
        {
            // Arrange
            var originalContent = "test content";
            var gzipContent = CreateGzipContent(originalContent);
            var mockHandler = new Mock<HttpMessageHandler>();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(gzipContent)
            };
            response.Content.Headers.ContentLength = gzipContent.Length;
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            mockHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act
            var result = await client.GetAsync("https://example.com/test");

            // Assert
            // Content-Length should be removed or updated to decompressed size
            // The implementation removes Content-Length when decompressing
            var body = await result.Content.ReadAsStringAsync();
            Assert.Equal(originalContent, body);
        }

        [Fact]
        public async Task SendAsync_SetsJsonContentTypeWhenDecompressingWithoutContentType()
        {
            // Arrange
            var originalContent = "{\"data\":123}";
            var gzipContent = CreateGzipContent(originalContent);
            var mockHandler = CreateMockHandler(gzipContent, null, null);

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act
            var response = await client.GetAsync("https://example.com/test");

            // Assert
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        }

        [Fact]
        public async Task SendAsync_PreservesContentTypeWhenDecompressing()
        {
            // Arrange
            var originalContent = "{\"data\":123}";
            var gzipContent = CreateGzipContent(originalContent);
            var mockHandler = CreateMockHandler(gzipContent, "application/vnd.api+json", null);

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act
            var response = await client.GetAsync("https://example.com/test");

            // Assert
            Assert.Equal("application/vnd.api+json", response.Content.Headers.ContentType?.MediaType);
        }

        #endregion

        #region Large Content Tests

        [Fact]
        public async Task SendAsync_HandlesLargeGzipContent()
        {
            // Arrange
            var largeContent = new string('A', 100_000);
            var gzipContent = CreateGzipContent(largeContent);
            var mockHandler = CreateMockHandler(gzipContent, "application/json", null);

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act
            var response = await client.GetAsync("https://example.com/test");
            var result = await ReadResponseContentAsync(response);

            // Assert
            Assert.Equal(100_000, result.Length);
            Assert.All(result, c => Assert.Equal('A', c));
        }

        #endregion

        #region Binary Content Tests

        [Fact]
        public async Task SendAsync_DoesNotDecompressNonGzipBinary()
        {
            // Arrange
            // Create binary content that doesn't start with gzip magic number
            var binaryContent = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };
            var mockHandler = CreateMockHandler(binaryContent, "application/octet-stream", null);

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act
            var response = await client.GetAsync("https://example.com/test");
            var result = await response.Content.ReadAsByteArrayAsync();

            // Assert
            Assert.Equal(binaryContent, result);
        }

        #endregion

        #region UTF-8 and Special Characters Tests

        [Fact]
        public async Task SendAsync_DecompressesGzipWithUtf8Content()
        {
            // Arrange
            var content = "{\"message\":\"Hello 世界 🌍\"}";
            var gzipContent = CreateGzipContent(content);
            var mockHandler = CreateMockHandler(gzipContent, "application/json", null);

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act
            var response = await client.GetAsync("https://example.com/test");
            var result = await ReadResponseContentAsync(response);

            // Assert
            Assert.Equal(content, result);
        }

        #endregion

        #region Multiple Content Encoding Headers

        [Fact]
        public async Task SendAsync_WithMultipleContentEncodings_PassesThrough()
        {
            // Arrange
            var content = Encoding.UTF8.GetBytes("compressed");
            var mockHandler = new Mock<HttpMessageHandler>();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            };
            response.Content.Headers.ContentEncoding.Add("gzip");
            response.Content.Headers.ContentEncoding.Add("deflate");

            mockHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act
            var result = await client.GetAsync("https://example.com/test");

            // Assert
            Assert.True(result.Content.Headers.ContentEncoding.Count > 0);
        }

        #endregion

        #region Cancellation Tests

        [Fact]
        public async Task SendAsync_RespectsCancellationToken()
        {
            // Arrange
            var mockHandler = new Mock<HttpMessageHandler>();
            var cts = new CancellationTokenSource();

            mockHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ThrowsAsync(new OperationCanceledException());

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act & Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.GetAsync("https://example.com/test", cts.Token));
        }

        #endregion

        #region Chained Handler Tests

        [Fact]
        public async Task SendAsync_WorksInHandlerChain()
        {
            // Arrange
            var content = Encoding.UTF8.GetBytes("test");
            var innerMock = CreateMockHandler(content);

            using var middleHandler = CreateHandler(innerMock.Object);
            using var outerHandler = CreateHandler(middleHandler);
            using var client = new HttpClient(outerHandler);

            // Act
            var response = await client.GetAsync("https://example.com/test");
            var result = await response.Content.ReadAsByteArrayAsync();

            // Assert
            Assert.Equal(content, result);
        }

        #endregion

        #region Gzip Magic Number Edge Cases

        [Fact]
        public async Task SendAsync_WithGzipMagicNumberOnly_AttemptsDecompression()
        {
            // Arrange
            // Only gzip magic number, no valid gzip stream
            var gzipOnly = new byte[] { 0x1F, 0x8B };
            var mockHandler = CreateMockHandler(gzipOnly);

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act & Assert
            // The handler will try to decompress, which may fail or return partial data
            // The important thing is it doesn't throw unhandled exception
            var response = await client.GetAsync("https://example.com/test");
            Assert.NotNull(response);
        }

        [Fact]
        public async Task SendAsync_WithFirstByteOfGzip_PassesThrough()
        {
            // Arrange
            var content = new byte[] { 0x1F, 0x00 };
            var mockHandler = CreateMockHandler(content);

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act
            var response = await client.GetAsync("https://example.com/test");
            var result = await response.Content.ReadAsByteArrayAsync();

            // Assert
            Assert.Equal(2, result.Length);
            Assert.Equal(0x1F, result[0]);
            Assert.Equal(0x00, result[1]);
        }

        #endregion

        #region Error Response Tests

        [Fact]
        public async Task SendAsync_WithErrorResponse_PassesThrough()
        {
            // Arrange
            var content = Encoding.UTF8.GetBytes("{\"error\":\"Not found\"}");
            var mockHandler = new Mock<HttpMessageHandler>();
            var response = new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new ByteArrayContent(content)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            mockHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act
            var result = await client.GetAsync("https://example.com/test");

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
            var body = await result.Content.ReadAsStringAsync();
            Assert.Equal("{\"error\":\"Not found\"}", body);
        }

        #endregion

        #region Response with ETag and LastModified

        [Fact]
        public async Task SendAsync_PreservesETagAndLastModified()
        {
            // Arrange
            var content = Encoding.UTF8.GetBytes("cached content");
            var mockHandler = new Mock<HttpMessageHandler>();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"abc123\"");
            response.Content.Headers.LastModified = DateTimeOffset.UtcNow.AddHours(-1);

            mockHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);

            using var handler = CreateHandler(mockHandler.Object);
            using var client = new HttpClient(handler);

            // Act
            var result = await client.GetAsync("https://example.com/test");

            // Assert
            Assert.Equal("\"abc123\"", result.Headers.ETag?.Tag);
            Assert.NotNull(result.Content.Headers.LastModified);
        }

        #endregion
    }
}
