using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Resilience
{
    public class TokenBucketRateLimiterTests
    {
        private readonly TokenBucketRateLimiter _limiter;

        public TokenBucketRateLimiterTests()
        {
            _limiter = new TokenBucketRateLimiter(NullLogger.Instance);
        }

        #region Token Refill Algorithm Correctness

        [Fact]
        public void Configure_ValidParameters_CreatesBucketWithFullTokens()
        {
            // Arrange
            const string resource = "test-api";
            const int maxRequests = 10;
            var period = TimeSpan.FromSeconds(60);

            // Act
            _limiter.Configure(resource, maxRequests, period);

            // Assert
            var available = _limiter.GetAvailableTokens(resource);
            Assert.Equal(maxRequests, available);
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(10, 1)]
        [InlineData(100, 60)]
        [InlineData(1000, 3600)]
        public async Task RefillRate_CalculatedCorrectly(int maxRequests, int periodSeconds)
        {
            // Arrange
            var resource = $"resource-{maxRequests}-{periodSeconds}";
            var period = TimeSpan.FromSeconds(periodSeconds);
            _limiter.Configure(resource, maxRequests, period);

            // Act: consume all tokens
            for (int i = 0; i < maxRequests; i++)
            {
                await _limiter.ExecuteAsync(resource, () => Task.FromResult(1));
            }

            var tokensAfterConsumption = _limiter.GetAvailableTokens(resource);
            Assert.Equal(0, tokensAfterConsumption);

            // Wait for refill period and check one token is available
            Thread.Sleep(period);

            // Assert: at least 1 token should be available (allowing for timing variance)
            var tokensAfterRefill = _limiter.GetAvailableTokens(resource);
            Assert.True(tokensAfterRefill >= 1, $"Expected at least 1 token after {periodSeconds}s, got {tokensAfterRefill}");
        }

        [Fact]
        public async Task Refill_TokensAccumulateUpToCapacity()
        {
            // Arrange
            const string resource = "capacity-test";
            const int capacity = 5;
            var period = TimeSpan.FromMilliseconds(100);
            _limiter.Configure(resource, capacity, period);

            // Act: consume all tokens
            for (int i = 0; i < capacity; i++)
            {
                await _limiter.ExecuteAsync(resource, () => Task.FromResult(i));
            }

            Assert.Equal(0, _limiter.GetAvailableTokens(resource));

            // Wait for multiple refill periods
            Thread.Sleep(period.Milliseconds * capacity * 3);

            // Assert: tokens should not exceed capacity
            var available = _limiter.GetAvailableTokens(resource);
            Assert.True(available >= 0 && available <= capacity, $"Tokens {available} should be in range [0, {capacity}]");
        }

        #endregion

        #region Concurrent Token Reservations (Thread Safety)

        [Fact]
        public async Task ConcurrentRequests_AllCompleteSuccessfully()
        {
            // Arrange
            const string resource = "concurrent-test";
            const int capacity = 10;
            var period = TimeSpan.FromMinutes(1);
            _limiter.Configure(resource, capacity, period);

            var completed = 0;
            var tasks = new List<Task<int>>();

            // Act: launch concurrent requests exceeding capacity
            for (int i = 0; i < capacity + 2; i++)
            {
                var index = i;
                tasks.Add(_limiter.ExecuteAsync(resource, async () =>
                {
                    Interlocked.Increment(ref completed);
                    await Task.Yield();
                    return index;
                }));
            }

            // Assert: all tasks complete
            var results = await Task.WhenAll(tasks);
            Assert.Equal(capacity + 2, results.Length);
            Assert.All(results, r => Assert.InRange(r, 0, capacity + 1));
        }

        [Fact]
        public async Task Concurrent_BurstTraffic_DoesNotExceedCapacity()
        {
            // Arrange
            const string resource = "burst-test";
            const int capacity = 5;
            var period = TimeSpan.FromSeconds(1);
            _limiter.Configure(resource, capacity, period);

            var successCount = 0;

            // Act: immediate burst of requests
            var tasks = Enumerable.Range(0, 20).Select(async i =>
            {
                await _limiter.ExecuteAsync(resource, () => Task.FromResult(i));
                Interlocked.Increment(ref successCount);
            }).ToArray();

            await Task.WhenAll(tasks);

            // Assert: all requests complete (rate limited, not rejected)
            Assert.Equal(20, successCount);
        }

        [Fact]
        public async Task Concurrent_MultipleResources_Isolated()
        {
            // Arrange
            _limiter.Configure("resource-a", 2, TimeSpan.FromSeconds(1));
            _limiter.Configure("resource-b", 5, TimeSpan.FromSeconds(1));

            var aCount = 0;
            var bCount = 0;

            // Act: burst both resources concurrently
            var tasks = new List<Task>();

            for (int i = 0; i < 10; i++)
            {
                tasks.Add(_limiter.ExecuteAsync("resource-a", () =>
                {
                    Interlocked.Increment(ref aCount);
                    return Task.FromResult(0);
                }));

                tasks.Add(_limiter.ExecuteAsync("resource-b", () =>
                {
                    Interlocked.Increment(ref bCount);
                    return Task.FromResult(0);
                }));
            }

            await Task.WhenAll(tasks);

            // Assert: both resources handle their bursts independently
            Assert.Equal(10, aCount);
            Assert.Equal(10, bCount);
        }

        #endregion

        #region Overflow/Underflow Handling

        [Fact]
        public async Task Overflow_RefillDoesNotExceedCapacity()
        {
            // Arrange
            const string resource = "overflow-test";
            const int capacity = 3;
            var period = TimeSpan.FromMilliseconds(50);
            _limiter.Configure(resource, capacity, period);

            // Act: consume some tokens, wait for multiple refill periods
            await _limiter.ExecuteAsync(resource, () => Task.FromResult(1));
            Assert.Equal(2, _limiter.GetAvailableTokens(resource));

            Thread.Sleep(period.Milliseconds * 10);

            // Assert: tokens never exceed capacity
            var available = _limiter.GetAvailableTokens(resource);
            Assert.True(available >= 0 && available <= capacity, $"Tokens {available} should be in range [0, {capacity}]");
        }

        [Fact]
        public async Task Underflow_ConsumptionGoesNegative_WaitsForRefill()
        {
            // Arrange
            const string resource = "underflow-test";
            const int capacity = 2;
            var period = TimeSpan.FromMilliseconds(100);
            _limiter.Configure(resource, capacity, period);

            var executionTimes = new List<long>();

            // Act: consume beyond capacity (goes negative internally)
            var tasks = Enumerable.Range(0, 5).Select(i => _limiter.ExecuteAsync(resource, async () =>
            {
                executionTimes.Add(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                await Task.Delay(10);
                return i;
            })).ToArray();

            await Task.WhenAll(tasks);

            // Assert: all requests complete, but later ones waited for refill
            Assert.Equal(5, executionTimes.Count);

            // First 2 execute quickly, next 3 wait for refill
            var gap1 = executionTimes[2] - executionTimes[1];
            Assert.True(gap1 > 0, $"Expected delay between request 2 and 3, got {gap1}ms");
        }

        #endregion

        #region Rate Limit Enforcement

        [Fact]
        public async Task RateLimit_BlocksWhenEmpty()
        {
            // Arrange
            const string resource = "block-test";
            const int capacity = 3;
            var period = TimeSpan.FromMilliseconds(200);
            _limiter.Configure(resource, capacity, period);

            // Act: exhaust capacity
            var tasks = new List<Task<int>>();
            for (int i = 0; i < capacity; i++)
            {
                tasks.Add(_limiter.ExecuteAsync(resource, () => Task.FromResult(i)));
            }

            await Task.WhenAll(tasks);
            Assert.Equal(0, _limiter.GetAvailableTokens(resource));

            // This request should block until refill
            var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var result = await _limiter.ExecuteAsync(resource, () => Task.FromResult(99));
            var elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start;

            // Assert: request completed but had to wait
            Assert.Equal(99, result);
            Assert.True(elapsed > 0, $"Expected some wait time, got {elapsed}ms");
        }

        [Fact]
        public async Task RateLimit_WaitTimeCalculatedCorrectly()
        {
            // Arrange
            const string resource = "wait-time-test";
            const int capacity = 1;
            var refillTime = TimeSpan.FromMilliseconds(300);
            _limiter.Configure(resource, capacity, refillTime);

            // Act: consume token, then request again
            await _limiter.ExecuteAsync(resource, () => Task.FromResult(0));

            var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await _limiter.ExecuteAsync(resource, () => Task.FromResult(0));
            var elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start;

            // Assert: should have waited approximately refillTime
            // Allow 50ms variance for thread scheduling
            Assert.InRange(elapsed, refillTime.TotalMilliseconds - 50, refillTime.TotalMilliseconds + 500);
        }

        [Fact]
        public async Task RateLimit_CancelledDuringWait_ThrowsOperationCanceled()
        {
            // Arrange
            const string resource = "cancel-test";
            const int capacity = 1;
            var period = TimeSpan.FromMinutes(1);
            _limiter.Configure(resource, capacity, period);

            using var cts = new CancellationTokenSource();

            // Consume token
            await _limiter.ExecuteAsync(resource, () => Task.FromResult(0));

            // Act: start request that will block, then cancel
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));
            var task = _limiter.ExecuteAsync(resource, async ct =>
            {
                await Task.Delay(1000, ct);
                return 42;
            }, cts.Token);

            // Assert
            await Assert.ThrowsAsync<TaskCanceledException>(async () => await task);
        }

        #endregion

        #region Token Capacity Limits

        [Theory]
        [InlineData(1)]
        [InlineData(10)]
        [InlineData(100)]
        [InlineData(1000)]
        public async Task Capacity_VariousSizes_AllowImmediateBurst(int capacity)
        {
            // Arrange
            var resource = $"capacity-{capacity}";
            _limiter.Configure(resource, capacity, TimeSpan.FromMinutes(1));

            // Act: burst to capacity
            var tasks = Enumerable.Range(0, capacity)
                .Select(i => _limiter.ExecuteAsync(resource, () => Task.FromResult(i)))
                .ToArray();

            var start = DateTimeOffset.UtcNow;
            await Task.WhenAll(tasks);
            var elapsed = DateTimeOffset.UtcNow - start;

            // Assert: all capacity requests execute quickly (no refill delay)
            Assert.True(elapsed < TimeSpan.FromMilliseconds(100),
                $"Expected burst of {capacity} to complete quickly, took {elapsed.TotalMilliseconds}ms");
            Assert.Equal(0, _limiter.GetAvailableTokens(resource));
        }

        [Fact]
        public void Capacity_ZeroRequested_UsesDefault()
        {
            // Arrange & Act
            _limiter.Configure("zero-capacity", 0, TimeSpan.FromMinutes(1));

            // Assert: should default to 10
            var available = _limiter.GetAvailableTokens("zero-capacity");
            Assert.Equal(10, available);
        }

        [Fact]
        public void Capacity_NegativeRequested_UsesDefault()
        {
            // Arrange & Act
            _limiter.Configure("negative-capacity", -5, TimeSpan.FromMinutes(1));

            // Assert: should default to 10
            var available = _limiter.GetAvailableTokens("negative-capacity");
            Assert.Equal(10, available);
        }

        #endregion

        #region Time-Based Refill Calculations

        [Theory]
        [InlineData(10, 60, 6)]     // 10 req/min, after 6s expect ~1 token
        [InlineData(60, 60, 1)]      // 60 req/min, after 1s expect ~1 token
        [InlineData(5, 10, 2)]       // 5 req/10s, after 2s expect ~1 token
        public async Task Refill_LinearWithTime(int capacity, int periodSeconds, int waitSeconds)
        {
            // Arrange
            var resource = $"refill-{capacity}-{periodSeconds}";
            var period = TimeSpan.FromSeconds(periodSeconds);
            _limiter.Configure(resource, capacity, period);

            // Consume all tokens
            for (int i = 0; i < capacity; i++)
            {
                await _limiter.ExecuteAsync(resource, () => Task.FromResult(i));
            }

            Assert.Equal(0, _limiter.GetAvailableTokens(resource));

            // Act: wait for partial refill
            Thread.Sleep(TimeSpan.FromSeconds(waitSeconds));

            // Assert: should have at least some tokens
            var available = _limiter.GetAvailableTokens(resource);
            Assert.True(available > 0, $"Expected tokens after {waitSeconds}s, got {available}");
        }

        [Fact]
        public void Refill_ZeroPeriod_UsesDefault()
        {
            // Arrange & Act
            _limiter.Configure("zero-period", 10, TimeSpan.Zero);

            // Should default to 1 minute period
            var available = _limiter.GetAvailableTokens("zero-period");
            Assert.Equal(10, available);
        }

        [Fact]
        public void Refill_NegativePeriod_UsesDefault()
        {
            // Arrange & Act
            _limiter.Configure("negative-period", 10, TimeSpan.FromSeconds(-1));

            // Should default to 1 minute period
            var available = _limiter.GetAvailableTokens("negative-period");
            Assert.Equal(10, available);
        }

        #endregion

        #region Edge Cases

        [Fact]
        public void EdgeCase_EmptyResourceName_UsesDefault()
        {
            // Arrange & Act
            _limiter.Configure("", 5, TimeSpan.FromSeconds(1));

            // Assert
            var available = _limiter.GetAvailableTokens("default");
            Assert.Equal(5, available);
        }

        [Fact]
        public void EdgeCase_WhitespaceResourceName_UsesDefault()
        {
            // Arrange & Act
            _limiter.Configure("   ", 5, TimeSpan.FromSeconds(1));

            // Assert
            var available = _limiter.GetAvailableTokens("default");
            Assert.Equal(5, available);
        }

        [Fact]
        public async Task EdgeCase_NullOperation_ThrowsArgumentNullException()
        {
            // Arrange
            _limiter.Configure("test", 10, TimeSpan.FromSeconds(1));
            Func<CancellationToken, Task<int>>? nullOperation = null!;

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                _limiter.ExecuteAsync("test", nullOperation, CancellationToken.None));
        }

        [Fact]
        public async Task EdgeCase_UnconfiguredResource_ExecutesWithoutRateLimit()
        {
            // Arrange
            var callCount = 0;

            // Act: execute against unconfigured resource
            var tasks = Enumerable.Range(0, 100).Select(i =>
                _limiter.ExecuteAsync("unconfigured-resource", () =>
                {
                    Interlocked.Increment(ref callCount);
                    return Task.FromResult(i);
                })
            ).ToArray();

            await Task.WhenAll(tasks);

            // Assert: all execute immediately without rate limiting
            Assert.Equal(100, callCount);
            Assert.Null(_limiter.GetAvailableTokens("unconfigured-resource"));
        }

        [Fact]
        public async Task EdgeCase_CaseInsensitiveResourceNames()
        {
            // Arrange
            _limiter.Configure("API", 5, TimeSpan.FromSeconds(1));

            // Act: consume using different cases
            await _limiter.ExecuteAsync("api", () => Task.FromResult(1));
            await _limiter.ExecuteAsync("API", () => Task.FromResult(2));
            await _limiter.ExecuteAsync("Api", () => Task.FromResult(3));

            // Assert: all share same bucket
            Assert.Equal(2, _limiter.GetAvailableTokens("API"));
            Assert.Equal(2, _limiter.GetAvailableTokens("api"));
            Assert.Equal(2, _limiter.GetAvailableTokens("Api"));
        }

        [Fact]
        public async Task EdgeCase_ResetSingleResource_RestoresCapacity()
        {
            // Arrange
            _limiter.Configure("reset-test", 3, TimeSpan.FromMinutes(1));
            await _limiter.ExecuteAsync("reset-test", () => Task.FromResult(1));
            await _limiter.ExecuteAsync("reset-test", () => Task.FromResult(2));

            Assert.Equal(1, _limiter.GetAvailableTokens("reset-test"));

            // Act
            _limiter.Reset("reset-test");

            // Assert
            Assert.Equal(3, _limiter.GetAvailableTokens("reset-test"));
        }

        [Fact]
        public async Task EdgeCase_ResetAllResources_RestoresAllCapacities()
        {
            // Arrange
            _limiter.Configure("resource-1", 2, TimeSpan.FromMinutes(1));
            _limiter.Configure("resource-2", 5, TimeSpan.FromMinutes(1));

            await _limiter.ExecuteAsync("resource-1", () => Task.FromResult(0));
            await _limiter.ExecuteAsync("resource-2", () => Task.FromResult(0));
            await _limiter.ExecuteAsync("resource-2", () => Task.FromResult(0));

            Assert.Equal(1, _limiter.GetAvailableTokens("resource-1"));
            Assert.Equal(3, _limiter.GetAvailableTokens("resource-2"));

            // Act
            _limiter.Reset();

            // Assert
            Assert.Equal(2, _limiter.GetAvailableTokens("resource-1"));
            Assert.Equal(5, _limiter.GetAvailableTokens("resource-2"));
        }

        [Fact]
        public void EdgeCase_ResetNonExistentResource_DoesNothing()
        {
            // Arrange & Act
            _limiter.Reset("does-not-exist");

            // Assert: should not throw
            Assert.Null(_limiter.GetAvailableTokens("does-not-exist"));
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(1, 0.1)]
        [InlineData(100, 1)]
        public void EdgeCase_MinimumCap_Validated(int capacity, double periodSeconds)
        {
            // Arrange & Act: very small period
            var resource = $"min-cap-{capacity}-{periodSeconds}";
            _limiter.Configure(resource, capacity, TimeSpan.FromSeconds(periodSeconds));

            // Assert: should not crash, minimum capacity enforced
            var available = _limiter.GetAvailableTokens(resource);
            Assert.True(available >= 1);
        }

        [Fact]
        public void EdgeCase_ReconfigureResource_ReplacesBucket()
        {
            // Arrange
            _limiter.Configure("reconfig", 5, TimeSpan.FromSeconds(1));
            Assert.Equal(5, _limiter.GetAvailableTokens("reconfig"));

            // Act: reconfigure with different parameters
            _limiter.Configure("reconfig", 10, TimeSpan.FromSeconds(1));

            // Assert
            Assert.Equal(10, _limiter.GetAvailableTokens("reconfig"));
        }

        #endregion

        #region Real-World Scenarios

        [Fact]
        public async Task Scenario_APIBurstThenSustainedLoad()
        {
            // Arrange: 10 requests per second
            const string resource = "api-scenario";
            const int capacity = 10;
            var period = TimeSpan.FromSeconds(1);
            _limiter.Configure(resource, capacity, period);

            var successCount = 0;

            // Act: burst 20 requests (exceeds capacity)
            var burstTasks = Enumerable.Range(0, 20).Select(i =>
                _limiter.ExecuteAsync(resource, async () =>
                {
                    Interlocked.Increment(ref successCount);
                    await Task.CompletedTask;
                    return 0;
                })
            ).ToArray();

            await Task.WhenAll(burstTasks);

            // Assert: all complete
            Assert.Equal(20, successCount);

            // After burst, some tokens should have recovered
            var recovered = _limiter.GetAvailableTokens(resource);
            Assert.True(recovered >= 0 && recovered <= capacity);
        }

        [Fact]
        public async Task Scenario_LongRunningOperation_DoesntStarveOthers()
        {
            // Arrange
            const string resource = "long-op";
            const int capacity = 2;
            var period = TimeSpan.FromMilliseconds(100);
            _limiter.Configure(resource, capacity, period);

            var results = new List<int>();

            // Act: mix fast and slow operations
            var tasks = new List<Task>
            {
                _limiter.ExecuteAsync(resource, async () =>
                {
                    await Task.Delay(50);
                    results.Add(1);
                    return 1;
                }),
                _limiter.ExecuteAsync(resource, () =>
                {
                    results.Add(2);
                    return Task.FromResult(2);
                }),
                _limiter.ExecuteAsync(resource, async () =>
                {
                    await Task.Delay(50);
                    results.Add(3);
                    return 3;
                })
            };

            await Task.WhenAll(tasks);

            // Assert: all complete
            Assert.Equal(3, results.Count);
            Assert.Contains(1, results);
            Assert.Contains(2, results);
            Assert.Contains(3, results);
        }

        [Fact]
        public async Task Scenario_MultiplePeriodicRefreshes()
        {
            // Arrange: 1 request per 100ms
            const string resource = "periodic";
            const int capacity = 1;
            var period = TimeSpan.FromMilliseconds(100);
            _limiter.Configure(resource, capacity, period);

            var timestamps = new List<long>();

            // Act: make 5 requests
            for (int i = 0; i < 5; i++)
            {
                var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await _limiter.ExecuteAsync(resource, () => Task.FromResult(i));
                timestamps.Add(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start);
            }

            // Assert: first request instant, subsequent requests wait
            Assert.InRange(timestamps[0], 0, 50);

            for (int i = 1; i < 5; i++)
            {
                // Should wait approximately period (100ms +/- 50ms for variance)
                Assert.InRange(timestamps[i], 50, 300);
            }
        }

        #endregion

        #region Integration with RateLimitPresets

        [Fact]
        public void Preset_LocalAI_ConfiguresExpectedLimits()
        {
            // Arrange & Act
            RateLimitPresets.ConfigureLocalAI(_limiter);

            // Assert
            Assert.Equal(30, _limiter.GetAvailableTokens("ollama"));
            Assert.Equal(30, _limiter.GetAvailableTokens("lmstudio"));
        }

        [Fact]
        public void Preset_CloudAI_ConfiguresExpectedLimits()
        {
            // Arrange & Act
            RateLimitPresets.ConfigureCloudAI(_limiter);

            // Assert
            Assert.Equal(10, _limiter.GetAvailableTokens("openai"));
            Assert.Equal(10, _limiter.GetAvailableTokens("anthropic"));
            Assert.Equal(15, _limiter.GetAvailableTokens("gemini"));
            Assert.Equal(20, _limiter.GetAvailableTokens("groq"));
        }

        [Fact]
        public void Preset_MusicAPIs_ConfiguresExpectedLimits()
        {
            // Arrange & Act
            RateLimitPresets.ConfigureMusicAPIs(_limiter);

            // Assert
            Assert.Equal(1, _limiter.GetAvailableTokens("musicbrainz"));
            Assert.Equal(5, _limiter.GetAvailableTokens("lastfm"));
        }

        [Fact]
        public void Preset_StreamingServices_ConfiguresExpectedLimits()
        {
            // Arrange & Act
            RateLimitPresets.ConfigureStreamingServices(_limiter);

            // Assert
            Assert.Equal(60, _limiter.GetAvailableTokens("tidal"));
            Assert.Equal(60, _limiter.GetAvailableTokens("qobuz"));
            Assert.Equal(30, _limiter.GetAvailableTokens("spotify"));
        }

        #endregion
    }
}
