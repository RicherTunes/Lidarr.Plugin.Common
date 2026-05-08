#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.Base;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Coverage tests for BaseStreamingIndexer abstract class.
    /// Target: src/Base/BaseStreamingIndexer.cs
    /// Note: Tests avoiding InitializeAsync due to FluentValidation version mismatch
    /// between main project (9.5.4) and test project (11.12.0).
    /// </summary>
    public class BaseStreamingIndexerCovTests
    {
        #region Constructor Tests - Line 92: ArgumentNullException

        [Fact]
        public void Constructor_NullSettings_ThrowsArgumentNullException()
        {
            // Act & Assert - Line 92: throw new ArgumentNullException(nameof(settings))
            var ex = Assert.Throws<ArgumentNullException>(() => new TestStreamingIndexer(null));
            Assert.Equal("settings", ex.ParamName);
        }

        [Fact]
        public void Constructor_ValidSettings_CreatesInstance()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };

            // Act
            var indexer = new TestStreamingIndexer(settings);

            // Assert - verify indexer was created with correct properties
            Assert.Equal("TestService", indexer.ServiceNameValue);
            Assert.Equal("test", indexer.ProtocolNameValue);
        }

        [Fact]
        public void Constructor_WithCustomLogger_CreatesInstance()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var logger = NullLogger.Instance;

            // Act
            var indexer = new TestStreamingIndexer(settings, logger);

            // Assert - verify indexer was created
            Assert.NotNull(indexer);
        }

        [Fact]
        public void Constructor_WithHttpClientFactory_CreatesInstance()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var clientFactory = () => new System.Net.Http.HttpClient();

            // Act
            var indexer = new TestStreamingIndexer(settings, null, clientFactory);

            // Assert - verify indexer was created
            Assert.NotNull(indexer);
        }

        #endregion

        #region SearchAsync Tests - Line 270: InvalidOperationException

        [Fact]
        public async Task SearchAsync_NotInitialized_ThrowsInvalidOperationException()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);

            // Act & Assert - Line 270: throw new InvalidOperationException
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => indexer.SearchAsync("test"));
            Assert.Contains("not initialized", ex.Message);
            Assert.Contains("TestService", ex.Message);
        }

        [Fact]
        public async Task SearchAsync_AfterSetInitialized_ReturnsResults()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            indexer.SetInitialized(true);
            indexer.SearchAlbumsResult = new List<StreamingAlbum>
            {
                new StreamingAlbum { Id = "1", Title = "Album 1" }
            };

            // Act
            var result = await indexer.SearchAsync("test");

            // Assert
            Assert.Single(result);
            Assert.Equal("Album 1", result[0].Title);
        }

        [Fact]
        public async Task SearchAsync_NullQuery_ReturnsEmptyList()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            indexer.SetInitialized(true);

            // Act
            var result = await indexer.SearchAsync(null);

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public async Task SearchAsync_WhitespaceQuery_ReturnsEmptyList()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            indexer.SetInitialized(true);

            // Act
            var result = await indexer.SearchAsync("   ");

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public async Task SearchAsync_EmptyQuery_ReturnsEmptyList()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            indexer.SetInitialized(true);

            // Act
            var result = await indexer.SearchAsync(string.Empty);

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public async Task SearchAsync_ValidQuery_ReturnsProcessedResults()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            indexer.SetInitialized(true);
            indexer.SearchAlbumsResult = new List<StreamingAlbum>
            {
                new StreamingAlbum { Id = "1", Title = "Album 1", Artist = new StreamingArtist { Name = "Artist" } }
            };

            // Act
            var result = await indexer.SearchAsync("test query");

            // Assert
            Assert.Single(result);
            Assert.Equal("Album 1", result[0].Title);
        }

        [Fact]
        public async Task SearchAsync_QueryWithExtraWhitespace_NormalizesQuery()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            indexer.SetInitialized(true);
            indexer.SearchAlbumsResult = new List<StreamingAlbum>();

            // Act
            await indexer.SearchAsync("  test   query  ");

            // Assert - PreprocessQuery should trim and normalize whitespace
            Assert.NotNull(indexer.LastSearchQuery);
            Assert.DoesNotContain("  ", indexer.LastSearchQuery);
        }

        [Fact]
        public async Task SearchAsync_SearchException_Propagates()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            indexer.SetInitialized(true);
            indexer.ThrowOnSearch = true;

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(() => indexer.SearchAsync("test"));
        }

        [Fact]
        public async Task SearchAsync_ReturnsEmptyList_WhenSearchAlbumsReturnsNull()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            indexer.SetInitialized(true);
            indexer.SearchAlbumsResult = null;

            // Act
            var result = await indexer.SearchAsync("test");

            // Assert - PostprocessResults handles null gracefully
            Assert.NotNull(result);
        }

        #endregion

        #region SearchStreamAsync Tests

        [Fact]
        public async Task SearchStreamAsync_ValidQuery_YieldsResults()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            indexer.SetInitialized(true);
            indexer.SearchAlbumsResult = new List<StreamingAlbum>
            {
                new StreamingAlbum { Id = "1", Title = "Album 1" },
                new StreamingAlbum { Id = "2", Title = "Album 2" }
            };

            // Act
            var results = new List<StreamingAlbum>();
            await foreach (var album in indexer.SearchStreamAsync("test"))
            {
                results.Add(album);
            }

            // Assert
            Assert.Equal(2, results.Count);
        }

        [Fact]
        public async Task SearchStreamAsync_WithCancellationToken_CanBeCancelled()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            indexer.SetInitialized(true);
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
                await foreach (var album in indexer.SearchStreamAsync("test", cts.Token))
                {
                    results.Add(album);
                }
            });

            // Assert - cancellation should throw OperationCanceledException
            Assert.IsType<OperationCanceledException>(ex);
        }

        #endregion

        #region PreprocessQuery Tests

        [Fact]
        public void PreprocessQuery_NullQuery_ReturnsEmptyString()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);

            // Act
            var result = indexer.PreprocessQueryPublic(null);

            // Assert
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public void PreprocessQuery_WhitespaceQuery_ReturnsEmptyString()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);

            // Act
            var result = indexer.PreprocessQueryPublic("   ");

            // Assert
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public void PreprocessQuery_QueryWithExtraWhitespace_NormalizesWhitespace()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);

            // Act
            var result = indexer.PreprocessQueryPublic("  test   query   here  ");

            // Assert
            Assert.Equal("test query here", result);
        }

        [Fact]
        public void PreprocessQuery_ValidQuery_ReturnsTrimmedQuery()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);

            // Act
            var result = indexer.PreprocessQueryPublic("test query");

            // Assert
            Assert.Equal("test query", result);
        }

        #endregion

        #region PostprocessResults Tests

        [Fact]
        public void PostprocessResults_NullResults_ReturnsEmptyList()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);

            // Act
            var result = indexer.PostprocessResultsPublic(null);

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public void PostprocessResults_EmptyResults_ReturnsEmptyList()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);

            // Act
            var result = indexer.PostprocessResultsPublic(new List<StreamingAlbum>());

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public void PostprocessResults_DuplicatesByTitleArtistYear_Deduplicates()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            var results = new List<StreamingAlbum>
            {
                new StreamingAlbum
                {
                    Title = "Album",
                    Artist = new StreamingArtist { Name = "Artist" },
                    ReleaseDate = new DateTime(2024, 1, 1),
                    TrackCount = 10
                },
                new StreamingAlbum
                {
                    Title = "Album",
                    Artist = new StreamingArtist { Name = "Artist" },
                    ReleaseDate = new DateTime(2024, 1, 1),
                    TrackCount = 5
                }
            };

            // Act
            var result = indexer.PostprocessResultsPublic(results);

            // Assert - should deduplicate by title + artist + year
            Assert.Single(result);
        }

        [Fact]
        public void PostprocessResults_SortsByTrackCountDescending()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            var results = new List<StreamingAlbum>
            {
                new StreamingAlbum { Title = "B", Artist = new StreamingArtist { Name = "Artist" }, TrackCount = 5 },
                new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "Artist" }, TrackCount = 5 },
                new StreamingAlbum { Title = "C", Artist = new StreamingArtist { Name = "Artist" }, TrackCount = 10 }
            };

            // Act
            var result = indexer.PostprocessResultsPublic(results);

            // Assert - sorted by track count descending, then title ascending
            Assert.Equal(3, result.Count);
            Assert.Equal(10, result[0].TrackCount); // Highest track count first
        }

        [Fact]
        public void PostprocessResults_SortsByTitleAscending_WhenTrackCountEqual()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            var results = new List<StreamingAlbum>
            {
                new StreamingAlbum { Title = "B", Artist = new StreamingArtist { Name = "Artist" }, TrackCount = 5 },
                new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "Artist" }, TrackCount = 5 },
                new StreamingAlbum { Title = "C", Artist = new StreamingArtist { Name = "Artist" }, TrackCount = 5 }
            };

            // Act
            var result = indexer.PostprocessResultsPublic(results);

            // Assert - sorted by title ascending when track count equal
            Assert.Equal(3, result.Count);
            Assert.Equal("A", result[0].Title);
            Assert.Equal("B", result[1].Title);
            Assert.Equal("C", result[2].Title);
        }

        [Fact]
        public void PostprocessResults_WithNullReleaseDate_DeduplicatesCorrectly()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            var results = new List<StreamingAlbum>
            {
                new StreamingAlbum
                {
                    Title = "Album",
                    Artist = new StreamingArtist { Name = "Artist" },
                    ReleaseDate = null,
                    TrackCount = 5
                },
                new StreamingAlbum
                {
                    Title = "Album",
                    Artist = new StreamingArtist { Name = "Artist" },
                    ReleaseDate = null,
                    TrackCount = 10
                }
            };

            // Act
            var result = indexer.PostprocessResultsPublic(results);

            // Assert - should deduplicate even with null release date
            Assert.Single(result);
        }

        [Fact]
        public void PostprocessResults_WithNullArtist_DeduplicatesCorrectly()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            var results = new List<StreamingAlbum>
            {
                new StreamingAlbum
                {
                    Title = "Album",
                    Artist = null,
                    ReleaseDate = new DateTime(2024, 1, 1),
                    TrackCount = 5
                },
                new StreamingAlbum
                {
                    Title = "Album",
                    Artist = null,
                    ReleaseDate = new DateTime(2024, 1, 1),
                    TrackCount = 10
                }
            };

            // Act
            var result = indexer.PostprocessResultsPublic(results);

            // Assert - should deduplicate even with null artist
            Assert.Single(result);
        }

        #endregion

        #region HandleRateLimitAsync Tests

        [Fact]
        public async Task HandleRateLimitAsync_Default_ImposesDelay()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            var start = DateTime.UtcNow;

            // Act
            await indexer.HandleRateLimitAsyncPublic();

            // Assert - default delay is 100ms
            var elapsed = DateTime.UtcNow - start;
            Assert.True(elapsed.TotalMilliseconds >= 90, $"Expected >= 90ms, got {elapsed.TotalMilliseconds}ms");
        }

        #endregion

        #region CreateRequest Tests

        [Fact]
        public void CreateRequest_WithEndpoint_CreatesGetRequest()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);

            // Act
            var request = indexer.CreateRequestPublic("/search");

            // Assert
            Assert.Equal("GET", request.Method.Method);
            Assert.Contains("api.test.com", request.RequestUri.ToString());
        }

        [Fact]
        public void CreateRequest_WithQueryParams_IncludesQueryParameters()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            var queryParams = new Dictionary<string, string>
            {
                ["q"] = "test query",
                ["limit"] = "10"
            };

            // Act
            var request = indexer.CreateRequestPublic("/search", queryParams);

            // Assert
            // Use AbsoluteUri (escaped form) — ToString() decodes %20 back to space.
            // The exact encoding form (+ vs %20) is incidental; we just verify the
            // query parameter made it through.
            var uri = request.RequestUri.AbsoluteUri;
            Assert.Contains("q=test%20query", uri);
            Assert.Contains("limit=10", uri);
        }

        [Fact]
        public void CreateRequest_WithNullQueryParams_CreatesRequest()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);

            // Act
            var request = indexer.CreateRequestPublic("/search", null);

            // Assert
            Assert.Contains("api.test.com", request.RequestUri.ToString());
        }

        #endregion

        #region FetchPagedAsync Tests

        [Fact]
        public async Task FetchPagedAsync_FetcherReturnsEmpty_StopsFetching()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            int callCount = 0;

            // Act
            var results = new List<string>();
            await foreach (var item in indexer.FetchPagedAsyncPublic<string>(offset =>
            {
                callCount++;
                return Task.FromResult<IReadOnlyList<string>>(offset > 0 ? Array.Empty<string>() : new[] { "a", "b" });
            }, pageSize: 10))
            {
                results.Add(item);
            }

            // Assert - should stop when fetcher returns empty
            Assert.Equal(2, results.Count);
            Assert.Equal(2, callCount); // First call returns items, second returns empty
        }

        [Fact]
        public async Task FetchPagedAsync_NullFetcher_YieldsNoItems()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);

            // Act
            var results = new List<int>();
            await foreach (var item in indexer.FetchPagedAsyncPublic<int>(null, 10))
            {
                results.Add(item);
            }

            // Assert
            Assert.Empty(results);
        }

        [Fact]
        public async Task FetchPagedAsync_WithCancellationToken_CanBeCancelled()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act
            var results = new List<int>();
            var ex = await Record.ExceptionAsync(async () =>
            {
                await foreach (var item in indexer.FetchPagedAsyncPublic<int>(offset =>
                    Task.FromResult<IReadOnlyList<int>>(new[] { 1, 2 }), 10, cts.Token))
                {
                    results.Add(item);
                }
            });

            // Assert
            Assert.IsType<OperationCanceledException>(ex);
        }

        [Fact]
        public async Task FetchPagedAsync_MultiplePages_YieldsAllItems()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            int callCount = 0;

            // Act
            var results = new List<int>();
            await foreach (var item in indexer.FetchPagedAsyncPublic<int>(offset =>
            {
                callCount++;
                if (offset == 0) return Task.FromResult<IReadOnlyList<int>>(new[] { 1, 2 });
                if (offset == 10) return Task.FromResult<IReadOnlyList<int>>(new[] { 3, 4 });
                return Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
            }, pageSize: 10))
            {
                results.Add(item);
            }

            // Assert
            Assert.Equal(new[] { 1, 2, 3, 4 }, results);
            Assert.Equal(3, callCount); // Two pages + empty termination
        }

        [Fact]
        public async Task FetchPagedAsync_NullPage_StopsFetching()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);
            int callCount = 0;

            // Act
            var results = new List<int>();
            await foreach (var item in indexer.FetchPagedAsyncPublic<int>(offset =>
            {
                callCount++;
                return Task.FromResult<IReadOnlyList<int>>(offset == 0 ? new[] { 1 } : null);
            }, pageSize: 10))
            {
                results.Add(item);
            }

            // Assert - should stop when fetcher returns null
            Assert.Single(results);
        }

        #endregion

        #region SearchTracksStreamAsync Tests

        [Fact]
        public async Task SearchTracksStreamAsync_ValidQuery_YieldsResults()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings)
            {
                SearchTracksResult = new List<StreamingTrack>
                {
                    new StreamingTrack { Id = "1", Title = "Track 1" },
                    new StreamingTrack { Id = "2", Title = "Track 2" }
                }
            };

            // Act
            var results = new List<StreamingTrack>();
            await foreach (var track in indexer.SearchTracksStreamAsyncPublic("test"))
            {
                results.Add(track);
            }

            // Assert
            Assert.Equal(2, results.Count);
        }

        [Fact]
        public async Task SearchTracksStreamAsync_WithCancellationToken_CanBeCancelled()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings)
            {
                SearchTracksResult = new List<StreamingTrack>
                {
                    new StreamingTrack { Id = "1", Title = "Track 1" }
                }
            };
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act
            var results = new List<StreamingTrack>();
            var ex = await Record.ExceptionAsync(async () =>
            {
                await foreach (var track in indexer.SearchTracksStreamAsyncPublic("test", cts.Token))
                {
                    results.Add(track);
                }
            });

            // Assert
            Assert.IsType<OperationCanceledException>(ex);
        }

        #endregion

        #region Dispose Tests

        [Fact]
        public void Dispose_CalledOnce_DoesNotThrow()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);

            // Act & Assert - should not throw
            indexer.Dispose();
        }

        [Fact]
        public void Dispose_CalledMultipleTimes_DoesNotThrow()
        {
            // Arrange
            var settings = new TestStreamingSettings { BaseUrl = "https://api.test.com" };
            var indexer = new TestStreamingIndexer(settings);

            // Act & Assert - should not throw on multiple disposes
            indexer.Dispose();
            indexer.Dispose();
            indexer.Dispose();
        }

        #endregion

        #region Test Implementation

        private sealed class TestStreamingSettings : BaseStreamingSettings
        {
        }

        private sealed class TestStreamingIndexer : BaseStreamingIndexer<TestStreamingSettings>
        {
            private static readonly string _serviceName = "TestService";
            private static readonly string _protocolName = "test";

            public TestStreamingIndexer(TestStreamingSettings settings, ILogger logger = null, Func<System.Net.Http.HttpClient> httpClientFactory = null)
                : base(settings, logger, httpClientFactory)
            {
            }

            protected override string ServiceName => _serviceName;
            protected override string ProtocolName => _protocolName;

            // Expose for testing
            public string ServiceNameValue => _serviceName;
            public string ProtocolNameValue => _protocolName;

            public List<StreamingAlbum> SearchAlbumsResult { get; set; } = new();
            public List<StreamingTrack> SearchTracksResult { get; set; } = new();
            public bool ThrowOnSearch { get; set; } = false;
            public string LastSearchQuery { get; private set; }

            // Allow tests to set initialization state
            public void SetInitialized(bool initialized)
            {
                var field = typeof(BaseStreamingIndexer<TestStreamingSettings>)
                    .GetField("_isInitialized", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                field?.SetValue(this, initialized);
            }

            protected override Task<bool> AuthenticateAsync()
            {
                return Task.FromResult(true);
            }

            protected override Task<List<StreamingAlbum>> SearchAlbumsAsync(string searchTerm)
            {
                LastSearchQuery = searchTerm;

                if (ThrowOnSearch)
                    throw new InvalidOperationException("Search failed");

                return Task.FromResult(SearchAlbumsResult);
            }

            protected override Task<List<StreamingTrack>> SearchTracksAsync(string searchTerm)
            {
                return Task.FromResult(SearchTracksResult);
            }

            protected override Task<StreamingAlbum> GetAlbumDetailsAsync(string albumId)
            {
                return Task.FromResult<StreamingAlbum>(null);
            }

            protected override FluentValidation.Results.ValidationResult ValidateSettings(TestStreamingSettings settings)
            {
                return new FluentValidation.Results.ValidationResult();
            }

            // Expose protected methods for testing
            public string PreprocessQueryPublic(string query) => PreprocessQuery(query);
            public List<StreamingAlbum> PostprocessResultsPublic(List<StreamingAlbum> results) => PostprocessResults(results);
            public Task HandleRateLimitAsyncPublic() => HandleRateLimitAsync();
            public System.Net.Http.HttpRequestMessage CreateRequestPublic(string endpoint, Dictionary<string, string> queryParams = null)
                => CreateRequest(endpoint, queryParams);
            public IAsyncEnumerable<T> FetchPagedAsyncPublic<T>(Func<int, Task<IReadOnlyList<T>>> fetchPageAsync, int pageSize, CancellationToken cancellationToken = default)
                => FetchPagedAsync(fetchPageAsync, pageSize, cancellationToken);
            public IAsyncEnumerable<StreamingTrack> SearchTracksStreamAsyncPublic(string query, CancellationToken cancellationToken = default)
                => SearchTracksStreamAsync(query, cancellationToken);
        }

        #endregion
    }
}
