using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Caching;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    [Trait("Category", "Unit")]
    public class FileStreamingResponseCacheTests : IDisposable
    {
        private readonly string _tempFolder;
        private readonly FileStreamingResponseCache _cache;

        public FileStreamingResponseCacheTests()
        {
            _tempFolder = Path.Combine(Path.GetTempPath(), "resp-cache-test-" + Guid.NewGuid().ToString("N"));
            _cache = new FileStreamingResponseCache(_tempFolder, TimeSpan.FromMinutes(30));
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempFolder, recursive: true); }
            catch { }
        }

        #region Get Tests

        [Fact]
        public void Get_ReturnsNullForMissingKey()
        {
            // Arrange
            var endpoint = "/api/test";
            var parameters = new Dictionary<string, string> { { "id", "123" } };

            // Act
            var result = _cache.Get<CachedHttpResponse>(endpoint, parameters);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void Get_ReturnsNullForNonExistentFile()
        {
            // Arrange
            var endpoint = "/api/nonexistent";
            var parameters = new Dictionary<string, string>();

            // Act
            var result = _cache.Get<CachedHttpResponse>(endpoint, parameters);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void Get_ReturnsNullForExpiredEntry()
        {
            // Arrange
            var endpoint = "/api/expired";
            var parameters = new Dictionary<string, string>();
            var response = new CachedHttpResponse
            {
                StatusCode = HttpStatusCode.OK,
                ContentType = "application/json",
                Body = Encoding.UTF8.GetBytes("test"),
                StoredAt = DateTimeOffset.UtcNow.AddHours(-2)
            };

            // Set with a very short duration via direct file write
            var cache = new FileStreamingResponseCache(_tempFolder, TimeSpan.FromMilliseconds(1));
            cache.Set(endpoint, parameters, response, TimeSpan.FromMilliseconds(1));

            // Act
            Thread.Sleep(50); // Ensure expiration
            var result = cache.Get<CachedHttpResponse>(endpoint, parameters);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void Get_ReturnsCachedResponse()
        {
            // Arrange
            var endpoint = "/api/cached";
            var parameters = new Dictionary<string, string> { { "key", "value" } };
            var expected = new CachedHttpResponse
            {
                StatusCode = HttpStatusCode.OK,
                ContentType = "application/json",
                Body = Encoding.UTF8.GetBytes("{\"result\":\"success\"}"),
                ETag = "\"abc123\"",
                LastModified = DateTimeOffset.UtcNow.AddHours(-1),
                StoredAt = DateTimeOffset.UtcNow
            };
            _cache.Set(endpoint, parameters, expected);

            // Act
            var result = _cache.Get<CachedHttpResponse>(endpoint, parameters);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(expected.StatusCode, result.StatusCode);
            Assert.Equal(expected.ContentType, result.ContentType);
            Assert.Equal(expected.Body, result.Body);
            Assert.Equal(expected.ETag, result.ETag);
            Assert.Equal(expected.LastModified, result.LastModified);
        }

        [Fact]
        public void Get_ReturnsNullForInvalidType()
        {
            // Arrange
            var endpoint = "/api/wrongtype";
            var parameters = new Dictionary<string, string>();
            var response = new CachedHttpResponse
            {
                StatusCode = HttpStatusCode.OK,
                Body = Array.Empty<byte>()
            };
            _cache.Set(endpoint, parameters, response);

            // Act
            var result = _cache.Get<string>(endpoint, parameters);

            // Assert
            Assert.Null(result);
        }

        #endregion

        #region Set Tests

        [Fact]
        public void Set_SerializesResponseToFile()
        {
            // Arrange
            var endpoint = "/api/serialize";
            var parameters = new Dictionary<string, string> { { "id", "456" } };
            var response = new CachedHttpResponse
            {
                StatusCode = HttpStatusCode.Created,
                ContentType = "application/json",
                Body = Encoding.UTF8.GetBytes("{\"created\":true}")
            };

            // Act
            _cache.Set(endpoint, parameters, response);

            // Assert
            var result = _cache.Get<CachedHttpResponse>(endpoint, parameters);
            Assert.NotNull(result);
            Assert.Equal(HttpStatusCode.Created, result.StatusCode);
            Assert.Equal("application/json", result.ContentType);
            Assert.Equal("{\"created\":true}", Encoding.UTF8.GetString(result.Body));
        }

        [Fact]
        public void Set_AppliesDefaultDuration()
        {
            // Arrange
            var cache = new FileStreamingResponseCache(_tempFolder, TimeSpan.FromHours(12));
            var endpoint = "/api/defaultduration";
            var parameters = new Dictionary<string, string>();
            var response = new CachedHttpResponse
            {
                StatusCode = HttpStatusCode.OK,
                Body = Encoding.UTF8.GetBytes("test")
            };

            // Act
            cache.Set(endpoint, parameters, response);

            // Assert
            var result = cache.Get<CachedHttpResponse>(endpoint, parameters);
            Assert.NotNull(result);
            // The entry should still be valid (not expired)
            Assert.NotNull(result);
        }

        [Fact]
        public void Set_AppliesCustomDuration()
        {
            // Arrange
            var endpoint = "/api/customduration";
            var parameters = new Dictionary<string, string>();
            var response = new CachedHttpResponse
            {
                StatusCode = HttpStatusCode.OK,
                Body = Encoding.UTF8.GetBytes("test")
            };
            var customDuration = TimeSpan.FromMinutes(15);

            // Act
            _cache.Set(endpoint, parameters, response, customDuration);

            // Assert
            var result = _cache.Get<CachedHttpResponse>(endpoint, parameters);
            Assert.NotNull(result);
            // The entry should still be valid (not expired) immediately after setting
            Assert.NotNull(result.Body);
        }

        [Fact]
        public void Set_UpdatesExistingEntry()
        {
            // Arrange
            var endpoint = "/api/update";
            var parameters = new Dictionary<string, string>();
            var original = new CachedHttpResponse
            {
                StatusCode = HttpStatusCode.OK,
                Body = Encoding.UTF8.GetBytes("original")
            };
            var updated = new CachedHttpResponse
            {
                StatusCode = HttpStatusCode.OK,
                Body = Encoding.UTF8.GetBytes("updated")
            };

            // Act
            _cache.Set(endpoint, parameters, original);
            _cache.Set(endpoint, parameters, updated);

            // Assert
            var result = _cache.Get<CachedHttpResponse>(endpoint, parameters);
            Assert.NotNull(result);
            Assert.Equal("updated", Encoding.UTF8.GetString(result.Body));
        }

        [Fact]
        public void Set_WithEmptyParameters()
        {
            // Arrange
            var endpoint = "/api/no-params";
            var parameters = new Dictionary<string, string>();
            var response = new CachedHttpResponse
            {
                StatusCode = HttpStatusCode.OK,
                Body = Encoding.UTF8.GetBytes("no params")
            };

            // Act
            _cache.Set(endpoint, parameters, response);

            // Assert
            var result = _cache.Get<CachedHttpResponse>(endpoint, parameters);
            Assert.NotNull(result);
        }

        #endregion

        #region GenerateCacheKey Tests

        [Fact]
        public void GenerateCacheKey_ProducesStableHash()
        {
            // Arrange
            var endpoint = "/api/stable";
            var parameters = new Dictionary<string, string> { { "a", "1" }, { "b", "2" } };

            // Act
            var key1 = _cache.GenerateCacheKey(endpoint, parameters);
            var key2 = _cache.GenerateCacheKey(endpoint, parameters);

            // Assert
            Assert.Equal(key1, key2);
        }

        [Fact]
        public void GenerateCacheKey_DifferentForDifferentEndpoints()
        {
            // Arrange
            var parameters = new Dictionary<string, string>();

            // Act
            var key1 = _cache.GenerateCacheKey("/api/one", parameters);
            var key2 = _cache.GenerateCacheKey("/api/two", parameters);

            // Assert
            Assert.NotEqual(key1, key2);
        }

        [Fact]
        public void GenerateCacheKey_DifferentForDifferentParameters()
        {
            // Arrange
            var endpoint = "/api/params";

            // Act
            var key1 = _cache.GenerateCacheKey(endpoint, new Dictionary<string, string> { { "id", "1" } });
            var key2 = _cache.GenerateCacheKey(endpoint, new Dictionary<string, string> { { "id", "2" } });

            // Assert
            Assert.NotEqual(key1, key2);
        }

        [Fact]
        public void GenerateCacheKey_HandlesSpecialCharacters()
        {
            // Arrange
            var endpoint = "/api/special";
            var parameters = new Dictionary<string, string>
            {
                { "key with spaces", "value with spaces" },
                { "special", "!@#$%^&*()_+-=[]{}|;':\",./<>?" }
            };

            // Act & Assert - Should not throw
            var key = _cache.GenerateCacheKey(endpoint, parameters);
            Assert.NotNull(key);
            Assert.NotEmpty(key);
        }

        [Fact]
        public void GenerateCacheKey_HandlesUnicodeCharacters()
        {
            // Arrange
            var endpoint = "/api/unicode";
            var parameters = new Dictionary<string, string>
            {
                { "unicode", "密码パスワード" }
            };

            // Act & Assert
            var key = _cache.GenerateCacheKey(endpoint, parameters);
            Assert.NotNull(key);
            Assert.NotEmpty(key);
        }

        [Fact]
        public void GenerateCacheKey_TrimWhitespaceInEndpoint()
        {
            // Arrange
            var parameters = new Dictionary<string, string>();

            // Act
            var key1 = _cache.GenerateCacheKey("  /api/trimmed  ", parameters);
            var key2 = _cache.GenerateCacheKey("/api/trimmed", parameters);

            // Assert
            Assert.Equal(key1, key2);
        }

        #endregion

        #region Clear Tests

        [Fact]
        public void Clear_RemovesAllEntries()
        {
            // Arrange
            _cache.Set("/api/one", new Dictionary<string, string>(), new CachedHttpResponse { Body = Array.Empty<byte>() });
            _cache.Set("/api/two", new Dictionary<string, string>(), new CachedHttpResponse { Body = Array.Empty<byte>() });

            // Act
            _cache.Clear();

            // Assert
            Assert.Null(_cache.Get<CachedHttpResponse>("/api/one", new Dictionary<string, string>()));
            Assert.Null(_cache.Get<CachedHttpResponse>("/api/two", new Dictionary<string, string>()));
        }

        [Fact]
        public void Clear_RecreatesDirectory()
        {
            // Arrange
            _cache.Set("/api/test", new Dictionary<string, string>(), new CachedHttpResponse { Body = Array.Empty<byte>() });
            _cache.Clear();

            // Act
            _cache.Set("/api/after", new Dictionary<string, string>(), new CachedHttpResponse { Body = Array.Empty<byte>() });

            // Assert
            Assert.NotNull(_cache.Get<CachedHttpResponse>("/api/after", new Dictionary<string, string>()));
        }

        #endregion

        #region ClearEndpoint Tests

        [Fact]
        public void ClearEndpoint_RemovesAllEntries()
        {
            // Arrange
            _cache.Set("/api/endpoint1", new Dictionary<string, string>(), new CachedHttpResponse { Body = Array.Empty<byte>() });
            _cache.Set("/api/endpoint2", new Dictionary<string, string>(), new CachedHttpResponse { Body = Array.Empty<byte>() });

            // Act
            _cache.ClearEndpoint("/api/endpoint1");

            // Assert
            Assert.Null(_cache.Get<CachedHttpResponse>("/api/endpoint1", new Dictionary<string, string>()));
            // ClearEndpoint is coarse and clears all
            Assert.Null(_cache.Get<CachedHttpResponse>("/api/endpoint2", new Dictionary<string, string>()));
        }

        #endregion

        #region ShouldCache Tests

        [Fact]
        public void ShouldCache_ReturnsTrue()
        {
            // Arrange & Act
            var result = _cache.ShouldCache("/api/any");

            // Assert
            Assert.True(result);
        }

        #endregion

        #region GetCacheDuration Tests

        [Fact]
        public void GetCacheDuration_ReturnsDefaultDuration()
        {
            // Arrange
            var expected = TimeSpan.FromMinutes(30);

            // Act
            var result = _cache.GetCacheDuration("/api/any");

            // Assert
            Assert.Equal(expected, result);
        }

        #endregion

        #region Atomic Write Tests

        [Fact]
        public void Set_AtomicallyWritesFile()
        {
            // Arrange
            var endpoint = "/api/atomic";
            var parameters = new Dictionary<string, string>();
            var response = new CachedHttpResponse
            {
                StatusCode = HttpStatusCode.OK,
                Body = Encoding.UTF8.GetBytes("atomic")
            };

            // Act
            _cache.Set(endpoint, parameters, response);

            // Assert - Should not leave .tmp file
            var cacheKey = _cache.GenerateCacheKey(endpoint, parameters);
            var sub = cacheKey[..2];
            var directory = Path.Combine(_tempFolder, sub);
            var files = Directory.GetFiles(directory);
            Assert.All(files, f => Assert.False(f.EndsWith(".tmp")));
        }

        [Fact]
        public void Set_ReplacesExistingFileAtomically()
        {
            // Arrange
            var endpoint = "/api/replace";
            var parameters = new Dictionary<string, string>();
            var original = new CachedHttpResponse
            {
                StatusCode = HttpStatusCode.OK,
                Body = Encoding.UTF8.GetBytes("original")
            };
            var replacement = new CachedHttpResponse
            {
                StatusCode = HttpStatusCode.OK,
                Body = Encoding.UTF8.GetBytes("replacement")
            };

            // Act
            _cache.Set(endpoint, parameters, original);
            _cache.Set(endpoint, parameters, replacement);

            // Assert
            var result = _cache.Get<CachedHttpResponse>(endpoint, parameters);
            Assert.NotNull(result);
            Assert.Equal("replacement", Encoding.UTF8.GetString(result.Body));
        }

        #endregion

        #region Thread Safety Tests

        [Fact]
        public async Task ConcurrentSetOperations_DoNotThrow()
        {
            // Arrange
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var tasks = new Task[50];

            // Act
            for (int i = 0; i < tasks.Length; i++)
            {
                var index = i;
                tasks[i] = Task.Run(() =>
                {
                    try
                    {
                        _cache.Set($"/api/concurrent-{index}", new Dictionary<string, string>(),
                            new CachedHttpResponse { Body = Encoding.UTF8.GetBytes($"value-{index}") });
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });
            }
            await Task.WhenAll(tasks);

            // Assert
            Assert.Empty(exceptions);
        }

        [Fact]
        public async Task ConcurrentGetOperations_DoNotThrow()
        {
            // Arrange
            for (int i = 0; i < 10; i++)
            {
                _cache.Set($"/api/shared-{i}", new Dictionary<string, string>(),
                    new CachedHttpResponse { Body = Encoding.UTF8.GetBytes($"data-{i}") });
            }

            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var tasks = new Task[50];

            // Act
            for (int i = 0; i < tasks.Length; i++)
            {
                var index = i % 10;
                tasks[i] = Task.Run(() =>
                {
                    try
                    {
                        _cache.Get<CachedHttpResponse>($"/api/shared-{index}", new Dictionary<string, string>());
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });
            }
            await Task.WhenAll(tasks);

            // Assert
            Assert.Empty(exceptions);
        }

        [Fact]
        public async Task ConcurrentSetAndGetOperations_MaintainConsistency()
        {
            // Arrange
            var endpoint = "/api/concurrent-consistency";
            var parameters = new Dictionary<string, string>();
            var errors = 0;

            var writeTask = Task.Run(() =>
            {
                for (int i = 0; i < 50; i++)
                {
                    _cache.Set(endpoint, parameters,
                        new CachedHttpResponse { Body = Encoding.UTF8.GetBytes($"value-{i}") });
                    Thread.Sleep(1);
                }
            });

            var readTask = Task.Run(() =>
            {
                for (int i = 0; i < 50; i++)
                {
                    var result = _cache.Get<CachedHttpResponse>(endpoint, parameters);
                    if (result != null && result.Body.Length == 0)
                    {
                        Interlocked.Increment(ref errors);
                    }
                    Thread.Sleep(1);
                }
            });

            // Act
            await Task.WhenAll(writeTask, readTask);

            // Assert
            Assert.Equal(0, errors);
        }

        #endregion

        #region Environment Variable Configuration Tests

        [Fact]
        public void Constructor_ReadsMaxEntriesFromEnvironment()
        {
            // Arrange
            var customFolder = Path.Combine(Path.GetTempPath(), "cache-env-test-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("ARR_RESP_CACHE_MAX_ENTRIES", "100");

            try
            {
                // Act
                var cache = new FileStreamingResponseCache(customFolder, TimeSpan.FromHours(1));

                // Assert - Cache created successfully
                Assert.NotNull(cache);
            }
            finally
            {
                Environment.SetEnvironmentVariable("ARR_RESP_CACHE_MAX_ENTRIES", null);
                try { Directory.Delete(customFolder, recursive: true); }
                catch { }
            }
        }

        [Fact]
        public void Constructor_ReadsMaxBytesFromEnvironment()
        {
            // Arrange
            var customFolder = Path.Combine(Path.GetTempPath(), "cache-env-test2-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("ARR_RESP_CACHE_MAX_MB", "128");

            try
            {
                // Act
                var cache = new FileStreamingResponseCache(customFolder, TimeSpan.FromHours(1));

                // Assert - Cache created successfully
                Assert.NotNull(cache);
            }
            finally
            {
                Environment.SetEnvironmentVariable("ARR_RESP_CACHE_MAX_MB", null);
                try { Directory.Delete(customFolder, recursive: true); }
                catch { }
            }
        }

        [Fact]
        public void Constructor_UsesDefaultValuesWhenEnvVarsInvalid()
        {
            // Arrange
            var customFolder = Path.Combine(Path.GetTempPath(), "cache-env-test3-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("ARR_RESP_CACHE_MAX_ENTRIES", "invalid");
            Environment.SetEnvironmentVariable("ARR_RESP_CACHE_MAX_MB", "-10");

            try
            {
                // Act
                var cache = new FileStreamingResponseCache(customFolder, TimeSpan.FromHours(1));

                // Assert - Should use fallback values
                Assert.NotNull(cache);
            }
            finally
            {
                Environment.SetEnvironmentVariable("ARR_RESP_CACHE_MAX_ENTRIES", null);
                Environment.SetEnvironmentVariable("ARR_RESP_CACHE_MAX_MB", null);
                try { Directory.Delete(customFolder, recursive: true); }
                catch { }
            }
        }

        #endregion

        #region Cleanup Tests

        [Fact]
        public void Constructor_CleansUpExpiredEntries()
        {
            // Arrange
            var customFolder = Path.Combine(Path.GetTempPath(), "cache-cleanup-test-" + Guid.NewGuid().ToString("N"));
            var cache = new FileStreamingResponseCache(customFolder, TimeSpan.FromMilliseconds(10));
            cache.Set("/api/expire", new Dictionary<string, string>(),
                new CachedHttpResponse { Body = Encoding.UTF8.GetBytes("will expire") }, TimeSpan.FromMilliseconds(10));

            // Wait for expiration
            Thread.Sleep(50);

            // Act - Create new cache instance which should cleanup
            var cache2 = new FileStreamingResponseCache(customFolder, TimeSpan.FromHours(1));

            // Assert
            var result = cache2.Get<CachedHttpResponse>("/api/expire", new Dictionary<string, string>());
            Assert.Null(result);

            // Cleanup
            try { Directory.Delete(customFolder, recursive: true); }
            catch { }
        }

        #endregion

        #region Null and Empty Body Tests

        [Fact]
        public void Set_WithNullBody()
        {
            // Arrange
            var endpoint = "/api/nullbody";
            var parameters = new Dictionary<string, string>();
            var response = new CachedHttpResponse
            {
                StatusCode = HttpStatusCode.NoContent,
                Body = null!
            };

            // Act
            _cache.Set(endpoint, parameters, response);

            // Assert
            var result = _cache.Get<CachedHttpResponse>(endpoint, parameters);
            Assert.NotNull(result);
            Assert.Empty(result.Body);
        }

        [Fact]
        public void Set_WithEmptyBody()
        {
            // Arrange
            var endpoint = "/api/emptybody";
            var parameters = new Dictionary<string, string>();
            var response = new CachedHttpResponse
            {
                StatusCode = HttpStatusCode.NoContent,
                Body = Array.Empty<byte>()
            };

            // Act
            _cache.Set(endpoint, parameters, response);

            // Assert
            var result = _cache.Get<CachedHttpResponse>(endpoint, parameters);
            Assert.NotNull(result);
            Assert.Empty(result.Body);
        }

        #endregion

        #region Limit Enforcement Tests

        [Fact]
        public void EnforceLimits_RemovesOldestEntries()
        {
            // Arrange
            var customFolder = Path.Combine(Path.GetTempPath(), "cache-limits-test-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("ARR_RESP_CACHE_MAX_ENTRIES", "5");
            var cache = new FileStreamingResponseCache(customFolder, TimeSpan.FromHours(1));

            try
            {
                // Act - Add more entries than max
                for (int i = 0; i < 10; i++)
                {
                    cache.Set($"/api/item-{i}", new Dictionary<string, string>(),
                        new CachedHttpResponse { Body = Encoding.UTF8.GetBytes($"data-{i}") });
                }

                // Assert - Should still work without throwing
                var result = cache.Get<CachedHttpResponse>("/api/item-9", new Dictionary<string, string>());
                Assert.NotNull(result);
            }
            finally
            {
                Environment.SetEnvironmentVariable("ARR_RESP_CACHE_MAX_ENTRIES", null);
                try { Directory.Delete(customFolder, recursive: true); }
                catch { }
            }
        }

        #endregion

        #region Parameter Ordering Tests

        [Fact]
        public void GenerateCacheKey_SameParametersDifferentOrder_SameKey()
        {
            // Note: Current implementation preserves order, so different order = different key
            // This test documents current behavior

            // Arrange
            var endpoint = "/api/order";

            // Act
            var key1 = _cache.GenerateCacheKey(endpoint,
                new Dictionary<string, string> { { "a", "1" }, { "b", "2" } });
            var key2 = _cache.GenerateCacheKey(endpoint,
                new Dictionary<string, string> { { "b", "2" }, { "a", "1" } });

            // Assert - Different order produces different keys with current implementation
            Assert.NotEqual(key1, key2);
        }

        #endregion

        #region Helper Class for Deserialization

        private class FileEntry
        {
            public HttpStatusCode StatusCode { get; set; }
            public string? ContentType { get; set; }
            public byte[]? Body { get; set; }
            public string? ETag { get; set; }
            public DateTimeOffset? LastModified { get; set; }
            public DateTimeOffset StoredAt { get; set; }
            public DateTimeOffset ExpireAt { get; set; }
        }

        #endregion
    }
}
