#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation.Results;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.Base;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Additional coverage tests for BaseStreamingIndexer uncovered paths.
    /// Targets: ExecuteRequestAsync, GetHttpClient override, InitializeAsync overload,
    /// SearchAlbumsStreamAsync virtual method.
    /// Complements BaseStreamingIndexerCovTests.cs which covers core functionality.
    /// </summary>
    public class BaseStreamingIndexerExecCovTests
    {
        #region ExecuteRequestAsync Tests

        [Fact]
        public async Task ExecuteRequestAsync_ValidRequest_ReturnsContent()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://httpbin.org" };
            var mockHandler = new MockHttpMessageHandler(HttpStatusCode.OK, "{\"result\":\"success\"}");
            var client = new HttpClient(mockHandler);
            var indexer = new TestStreamingIndexer(settings, null, () => client);
            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.test.com/endpoint");

            // Act
            var result = await indexer.ExecuteRequestAsyncPublic(request);

            // Assert
            Assert.Equal("{\"result\":\"success\"}", result);
        }

        [Fact]
        public async Task ExecuteRequestAsync_NotFoundResponse_ThrowsHttpRequestException()
        {
            // Arrange - Line 329: response.EnsureSuccessStatusCode()
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var mockHandler = new MockHttpMessageHandler(HttpStatusCode.NotFound, "Not found");
            var client = new HttpClient(mockHandler);
            var indexer = new TestStreamingIndexer(settings, null, () => client);
            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.test.com/endpoint");

            // Act & Assert
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => indexer.ExecuteRequestAsyncPublic(request));
            Assert.Contains("404", ex.Message);
        }

        [Fact]
        public async Task ExecuteRequestAsync_UnauthorizedResponse_ThrowsHttpRequestException()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var mockHandler = new MockHttpMessageHandler(HttpStatusCode.Unauthorized, "Unauthorized");
            var client = new HttpClient(mockHandler);
            var indexer = new TestStreamingIndexer(settings, null, () => client);
            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.test.com/endpoint");

            // Act & Assert
            await Assert.ThrowsAsync<HttpRequestException>(() => indexer.ExecuteRequestAsyncPublic(request));
        }

        [Fact]
        public async Task ExecuteRequestAsync_InternalServerError_ThrowsHttpRequestException()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var mockHandler = new MockHttpMessageHandler(HttpStatusCode.InternalServerError, "Server error");
            var client = new HttpClient(mockHandler);
            var indexer = new TestStreamingIndexer(settings, null, () => client);
            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.test.com/endpoint");

            // Act & Assert
            await Assert.ThrowsAsync<HttpRequestException>(() => indexer.ExecuteRequestAsyncPublic(request));
        }

        [Fact]
        public async Task ExecuteRequestAsync_Timeout_ExceptionMessageDoesNotLeakQuerySecrets()
        {
            // The wrapped HttpRequestException must scrub the request URI: streaming services
            // (e.g. Qobuz) carry credentials like user_auth_token in query strings, and the host
            // surfaces indexer exception messages unredacted.
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var client = new HttpClient(new ThrowingHttpMessageHandler(new TimeoutException("simulated timeout")));
            var indexer = new TestStreamingIndexer(settings, null, () => client);
            var request = new HttpRequestMessage(
                HttpMethod.Get, "https://api.test.com/endpoint?user_auth_token=SENTINEL_SECRET");

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => indexer.ExecuteRequestAsyncPublic(request));

            Assert.DoesNotContain("SENTINEL_SECRET", ex.Message);
            Assert.Contains("api.test.com", ex.Message); // host stays visible for diagnostics
        }

        [Fact]
        public async Task ExecuteRequestAsync_Cancellation_ExceptionMessageDoesNotLeakQuerySecrets()
        {
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var client = new HttpClient(new ThrowingHttpMessageHandler(new TaskCanceledException("simulated cancel")));
            var indexer = new TestStreamingIndexer(settings, null, () => client);
            var request = new HttpRequestMessage(
                HttpMethod.Get, "https://api.test.com/endpoint?user_auth_token=SENTINEL_SECRET");

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => indexer.ExecuteRequestAsyncPublic(request));

            Assert.DoesNotContain("SENTINEL_SECRET", ex.Message);
            Assert.Contains("api.test.com", ex.Message);
        }

        #endregion

        #region GetHttpClient Override Tests

        [Fact]
        public void GetHttpClient_DefaultFactory_ReturnsSharedClient()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);

            // Act
            var client1 = indexer.GetHttpClientPublic();
            var client2 = indexer.GetHttpClientPublic();

            // Assert - default factory returns shared client
            Assert.Same(client1, client2);
        }

        [Fact]
        public void GetHttpClient_CustomFactory_ReturnsCustomClient()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var customClient = new HttpClient();
            var indexer = new TestStreamingIndexer(settings, null, () => customClient);

            // Act
            var client = indexer.GetHttpClientPublic();

            // Assert
            Assert.Same(customClient, client);
        }

        [Fact]
        public void GetHttpClient_DifferentInstancesWithDifferentFactory_ReturnDifferentClients()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer1 = new TestStreamingIndexer(settings);
            var indexer2 = new TestStreamingIndexer(settings, null, () => new HttpClient());

            // Act
            var client1 = indexer1.GetHttpClientPublic();
            var client2 = indexer2.GetHttpClientPublic();

            // Assert - indexer1 uses shared, indexer2 uses custom
            Assert.NotSame(client1, client2);
        }

        #endregion

        #region InitializeAsync(CancellationToken) Tests

        [Fact]
        public async Task InitializeAsync_WithCancellationToken_NotCancelled_Succeeds()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            var cts = new CancellationTokenSource();

            // Act
            var result = await indexer.InitializeAsyncPublic(cts.Token);

            // Assert
            Assert.True(result.IsValid);
        }

        [Fact]
        public async Task InitializeAsync_WithCancellationToken_CancelledBeforeCall_ThrowsOperationCanceledException()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert
            await Assert.ThrowsAsync<OperationCanceledException>(() => indexer.InitializeAsyncPublic(cts.Token));
        }

        [Fact]
        public async Task InitializeAsync_AfterFirstCall_SkipsInitialization()
        {
            // Arrange - tests the _isInitialized lock at line ~242
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            indexer.InitializeCallCount = 0;

            // Act
            await indexer.InitializeAsyncPublic(CancellationToken.None);
            await indexer.InitializeAsyncPublic(CancellationToken.None);

            // Assert - InitializeAsync should only run once
            Assert.Equal(1, indexer.InitializeCallCount);
        }

        [Fact]
        public async Task InitializeAsync_InvalidSettings_ReturnsValidationErrors()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            indexer.ValidateSettingsResult = new ValidationResult
            {
                Errors = { new ValidationFailure("BaseUrl", "Invalid URL") }
            };

            // Act
            var result = await indexer.InitializeAsyncPublic(CancellationToken.None);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.PropertyName == "BaseUrl");
        }

        [Fact]
        public async Task InitializeAsync_AuthenticationFails_ReturnsAuthError()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            indexer.AuthenticateResult = false;

            // Act
            var result = await indexer.InitializeAsyncPublic(CancellationToken.None);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.PropertyName == "Authentication");
        }

        [Fact]
        public async Task InitializeAsync_ExceptionDuringInit_ReturnsErrorResult()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            indexer.ThrowOnInit = true;

            // Act
            var result = await indexer.InitializeAsyncPublic(CancellationToken.None);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.PropertyName == "Initialization");
        }

        #endregion

        #region SearchAlbumsStreamAsync Tests

        [Fact]
        public async Task SearchAlbumsStreamAsync_ValidQuery_YieldsAllResults()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            indexer.SearchAlbumsResult = new List<StreamingAlbum>
            {
                new StreamingAlbum { Id = "1", Title = "Album 1" },
                new StreamingAlbum { Id = "2", Title = "Album 2" },
                new StreamingAlbum { Id = "3", Title = "Album 3" }
            };

            // Act
            var results = new List<StreamingAlbum>();
            await foreach (var album in indexer.SearchAlbumsStreamAsyncPublic("test"))
            {
                results.Add(album);
            }

            // Assert
            Assert.Equal(3, results.Count);
        }

        [Fact]
        public async Task SearchAlbumsStreamAsync_EmptyResults_YieldsNothing()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            indexer.SearchAlbumsResult = new List<StreamingAlbum>();

            // Act
            var results = new List<StreamingAlbum>();
            await foreach (var album in indexer.SearchAlbumsStreamAsyncPublic("test"))
            {
                results.Add(album);
            }

            // Assert
            Assert.Empty(results);
        }

        [Fact]
        public async Task SearchAlbumsStreamAsync_NullResults_YieldsNothing()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            indexer.SearchAlbumsResult = null;

            // Act
            var results = new List<StreamingAlbum>();
            await foreach (var album in indexer.SearchAlbumsStreamAsyncPublic("test"))
            {
                results.Add(album);
            }

            // Assert
            Assert.Empty(results);
        }

        [Fact]
        public async Task SearchAlbumsStreamAsync_WithCancellationToken_CanBeCancelled()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            indexer.SearchAlbumsResult = new List<StreamingAlbum>
            {
                new StreamingAlbum { Id = "1", Title = "Album 1" }
            };
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act
            var results = new List<StreamingAlbum>();
            var ex = await Record.ExceptionAsync(async () =>
            {
                await foreach (var album in indexer.SearchAlbumsStreamAsyncPublic("test", cts.Token))
                {
                    results.Add(album);
                }
            });

            // Assert
            Assert.IsType<OperationCanceledException>(ex);
        }

        #endregion

        #region Test Infrastructure

        private sealed class TestStreamingSettings : BaseStreamingSettings
        {
        }

        private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
        {
            private readonly Exception _exception;

            public ThrowingHttpMessageHandler(Exception exception)
            {
                _exception = exception;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromException<HttpResponseMessage>(_exception);
        }

        private sealed class MockHttpMessageHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _statusCode;
            private readonly string _content;

            public MockHttpMessageHandler(HttpStatusCode statusCode, string content)
            {
                _statusCode = statusCode;
                _content = content;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(_statusCode)
                {
                    Content = new StringContent(_content)
                };
                return Task.FromResult(response);
            }
        }

        private sealed class TestStreamingIndexer : BaseStreamingIndexer<TestStreamingSettings>
        {
            private static readonly string _serviceName = "TestService";
            private static readonly string _protocolName = "test";

            public TestStreamingIndexer(TestStreamingSettings settings, ILogger logger = null, Func<HttpClient> httpClientFactory = null)
                : base(settings, logger, httpClientFactory)
            {
            }

            protected override string ServiceName => _serviceName;
            protected override string ProtocolName => _protocolName;

            public List<StreamingAlbum> SearchAlbumsResult { get; set; }
            public List<StreamingTrack> SearchTracksResult { get; set; } = new();
            public bool AuthenticateResult { get; set; } = true;
            public ValidationResult ValidateSettingsResult { get; set; } = new ValidationResult();
            public bool ThrowOnInit { get; set; } = false;
            public int InitializeCallCount { get; set; } = 0;

            protected override Task<bool> AuthenticateAsync()
            {
                return Task.FromResult(AuthenticateResult);
            }

            protected override Task<List<StreamingAlbum>> SearchAlbumsAsync(string searchTerm)
            {
                return Task.FromResult(SearchAlbumsResult ?? new List<StreamingAlbum>());
            }

            protected override Task<List<StreamingTrack>> SearchTracksAsync(string searchTerm)
            {
                return Task.FromResult(SearchTracksResult);
            }

            protected override Task<StreamingAlbum> GetAlbumDetailsAsync(string albumId)
            {
                return Task.FromResult<StreamingAlbum>(null);
            }

            protected override ValidationResult ValidateSettings(TestStreamingSettings settings)
            {
                InitializeCallCount++;
                if (ThrowOnInit)
                    throw new InvalidOperationException("Init failed");
                return ValidateSettingsResult;
            }

            // Expose protected members for testing
            public HttpClient GetHttpClientPublic() => GetHttpClient();
            public Task<string> ExecuteRequestAsyncPublic(HttpRequestMessage request) => ExecuteRequestAsync(request);
            public Task<ValidationResult> InitializeAsyncPublic(CancellationToken cancellationToken) => InitializeAsync(cancellationToken);
            public IAsyncEnumerable<StreamingAlbum> SearchAlbumsStreamAsyncPublic(string query, CancellationToken cancellationToken = default)
                => SearchAlbumsStreamAsync(query, cancellationToken);
        }

        #endregion
    }
}
