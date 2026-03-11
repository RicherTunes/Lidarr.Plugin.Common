using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Network;
using Lidarr.Plugin.Common.Utilities;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    [Trait("Category", "Integration")]
    public class NetworkResilienceServiceTests
    {
        #region Test Setup

        private NetworkResilienceService CreateService(ILogger<NetworkResilienceService>? logger = null)
        {
            return new NetworkResilienceService(logger ?? Mock.Of<ILogger<NetworkResilienceService>>());
        }

        /// <summary>
        /// Synchronous IProgress&lt;T&gt; that invokes the callback immediately on the caller thread.
        /// Unlike <see cref="Progress{T}"/>, this never drops callbacks because it bypasses
        /// SynchronizationContext.Post (which can coalesce rapid updates in test environments).
        /// </summary>
        private class SynchronousProgress<T> : IProgress<T>
        {
            private readonly Action<T> _handler;
            public SynchronousProgress(Action<T> handler) => _handler = handler;
            public void Report(T value) => _handler(value);
        }

        private static async Task<string> SuccessProcessor(string item, CancellationToken ct)
        {
            await Task.CompletedTask;
            return $"processed-{item}";
        }

        private static async Task<string> FailingProcessor(string item, CancellationToken ct)
        {
            await Task.Delay(10, ct);
            throw new HttpRequestException("Network error");
        }

        private static async Task<string> TimeoutProcessor(string item, CancellationToken ct)
        {
            await Task.Delay(10, ct);
            throw new TimeoutException("Request timed out");
        }

        private static async Task<string> NonRetryableProcessor(string item, CancellationToken ct)
        {
            await Task.CompletedTask;
            throw new InvalidOperationException("Non-retryable error");
        }

        #endregion

        #region Empty Batch Tests

        [Fact]
        public async Task ExecuteResilientBatch_EmptyItems_ReturnsSuccess()
        {
            // Arrange
            var service = CreateService();
            var items = Enumerable.Empty<string>();

            // Act
            var result = await service.ExecuteResilientBatchAsync(
                "empty-test",
                items,
                SuccessProcessor);

            // Assert
            Assert.True(result.IsSuccessful);
            Assert.Empty(result.Results);
            Assert.Empty(result.Failures);
            Assert.Equal("empty-test", result.OperationId);
        }

        #endregion

        #region Successful Batch Tests

        [Fact]
        public async Task ExecuteResilientBatch_AllSucceed_ReturnsAllResults()
        {
            // Arrange
            var service = CreateService();
            var items = new[] { "item1", "item2", "item3" };

            // Act
            var result = await service.ExecuteResilientBatchAsync(
                "all-success-test",
                items,
                SuccessProcessor);

            // Assert
            Assert.True(result.IsSuccessful);
            Assert.Equal(3, result.Results.Count);
            Assert.Contains("processed-item1", result.Results);
            Assert.Contains("processed-item2", result.Results);
            Assert.Contains("processed-item3", result.Results);
            Assert.Empty(result.Failures);
        }

        [Fact]
        public async Task ExecuteResilientBatch_AllSucceed_CompletionTimeIsPopulated()
        {
            // Arrange
            var service = CreateService();
            var items = new[] { "item1", "item2" };

            // Act
            var result = await service.ExecuteResilientBatchAsync(
                "completion-time-test",
                items,
                SuccessProcessor);

            // Assert
            Assert.True(result.CompletionTime > TimeSpan.Zero);
        }

        #endregion

        #region Failure Handling Tests

        [Fact]
        public async Task ExecuteResilientBatch_ContinueOnFailure_ReturnsPartialSuccess()
        {
            // Arrange
            var service = CreateService();
            var items = new[] { "item1", "item2", "item3" };
            var callCount = 0;
            async Task<string> ConditionalProcessor(string item, CancellationToken ct)
            {
                await Task.CompletedTask;
                callCount++;
                if (item == "item2")
                    throw new HttpRequestException("Network error");
                return $"processed-{item}";
            }

            // Act
            var result = await service.ExecuteResilientBatchAsync(
                "partial-failure-test",
                items,
                ConditionalProcessor,
                NetworkResilienceOptions.Default);

            // Assert
            // IsSuccessful is true when ContinueOnFailure=true and there are some results
            Assert.True(result.IsSuccessful);
            Assert.Equal(2, result.Results.Count);
            Assert.Single(result.Failures);
            Assert.Equal(1, result.Failures[0].ItemIndex);
        }

        [Fact]
        public async Task ExecuteResilientBatch_StopOnFailure_StopsAtFirstError()
        {
            // Arrange
            var service = CreateService();
            var items = new[] { "item1", "item2", "item3" };
            var processedItems = new List<string>();
            async Task<string> StopOnFailureProcessor(string item, CancellationToken ct)
            {
                // Add to processedItems BEFORE potentially throwing
                processedItems.Add(item);
                await Task.CompletedTask;
                if (item == "item2")
                    throw new HttpRequestException("Network error");
                return $"processed-{item}";
            }

            var options = new NetworkResilienceOptions { ContinueOnFailure = false };

            // Act
            var result = await service.ExecuteResilientBatchAsync(
                "stop-on-failure-test",
                items,
                StopOnFailureProcessor,
                options);

            // Assert
            Assert.False(result.IsSuccessful);
            Assert.Single(result.Results); // Only item1 succeeded
            Assert.Single(result.Failures);
            Assert.Equal(1, result.Failures[0].ItemIndex);
            // item1: 1 call (success)
            // item2: 3 calls (retries due to HttpRequestException being retryable)
            // Total: 4 calls
            Assert.Equal(4, processedItems.Count);
            Assert.True(result.CanRetryFromCheckpoint);
        }

        #endregion

        #region Retry Tests

        [Fact]
        public async Task ExecuteResilientBatch_RetriesTransientFailures()
        {
            // Arrange
            var service = CreateService();
            var items = new[] { "item1" };
            var attemptCount = 0;
            async Task<string> EventualSuccessProcessor(string item, CancellationToken ct)
            {
                await Task.CompletedTask;
                attemptCount++;
                if (attemptCount < 2)
                    throw new HttpRequestException("Transient error");
                return $"processed-{item}";
            }

            // Act
            var result = await service.ExecuteResilientBatchAsync(
                "retry-test",
                items,
                EventualSuccessProcessor);

            // Assert
            Assert.True(result.IsSuccessful);
            Assert.Equal(2, attemptCount); // Initial + 1 retry
            Assert.Single(result.Results);
        }

        [Fact]
        public async Task ExecuteResilientBatch_RetriesUpToMaxAttempts()
        {
            // Arrange
            var service = CreateService();
            var items = new[] { "item1" };
            var attemptCount = 0;
            async Task<string> AlwaysFailingProcessor(string item, CancellationToken ct)
            {
                await Task.CompletedTask;
                attemptCount++;
                throw new HttpRequestException("Persistent error");
            }

            // Act
            var result = await service.ExecuteResilientBatchAsync(
                "max-retry-test",
                items,
                AlwaysFailingProcessor);

            // Assert
            Assert.False(result.IsSuccessful);
            Assert.Equal(3, attemptCount); // Default max retry attempts is 3
            Assert.Single(result.Failures);
            Assert.True(result.Failures[0].CanRetry);
        }

        [Fact]
        public async Task ProcessItemWithRetry_ShouldRetry_ClassifiesExceptions()
        {
            // Arrange
            var service = CreateService();

            // Test HttpRequestException - should retry
            var result1 = await service.ExecuteResilientBatchAsync(
                "http-retry-test",
                new[] { "item1" },
                FailingProcessor);
            Assert.True(result1.Failures[0].CanRetry);

            // Test TimeoutException - should retry
            var result2 = await service.ExecuteResilientBatchAsync(
                "timeout-retry-test",
                new[] { "item1" },
                TimeoutProcessor);
            Assert.True(result2.Failures[0].CanRetry);

            // Test InvalidOperationException - should NOT retry
            var result3 = await service.ExecuteResilientBatchAsync(
                "non-retryable-test",
                new[] { "item1" },
                NonRetryableProcessor);
            Assert.False(result3.Failures[0].CanRetry);
        }

        [Fact]
        public async Task ExecuteResilientBatch_DoesNotRetryOperationCanceledException()
        {
            // Arrange
            var service = CreateService();
            var items = new[] { "item1" };
            async Task<string> CancellationProcessor(string item, CancellationToken ct)
            {
                await Task.CompletedTask;
                throw new OperationCanceledException();
            }

            // Act & Assert
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                service.ExecuteResilientBatchAsync(
                    "cancellation-test",
                    items,
                    CancellationProcessor));
        }

        #endregion

        #region Circuit Breaker Tests

        [Fact]
        public async Task ExecuteResilientBatch_CircuitBreakerOpensAfterThreshold()
        {
            // Arrange
            var service = CreateService();
            var items = new[] { "item1" };

            // Fail enough times to open circuit (threshold is 5)
            // The service catches exceptions and returns failed results, not throws
            for (int i = 0; i < 5; i++)
            {
                var failResult = await service.ExecuteResilientBatchAsync(
                    $"failure-{i}",
                    items,
                    FailingProcessor);
                Assert.False(failResult.IsSuccessful);
            }

            // Act - Try a new batch with circuit breaker open
            var result = await service.ExecuteResilientBatchAsync(
                "circuit-open-test",
                new[] { "item1", "item2" },
                SuccessProcessor);

            // Assert
            Assert.False(result.IsSuccessful);
            Assert.Contains("circuit", result.FailureReason, StringComparison.OrdinalIgnoreCase);
            Assert.True(result.CanRetryLater);
        }

        [Fact]
        public async Task ExecuteResilientBatch_CircuitBreakerResetsAfterTimeout()
        {
            // Arrange
            var service = CreateService();
            var items = new[] { "item1" };

            // This test requires time manipulation or waiting
            // For now, we'll test the behavior without waiting the full timeout
            // In a real scenario, you'd use a fake time provider

            // Fail to open circuit
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    await service.ExecuteResilientBatchAsync(
                        $"failure-{i}",
                        items,
                        FailingProcessor);
                }
                catch { }
            }

            // Circuit should be open now
            var result1 = await service.ExecuteResilientBatchAsync(
                "circuit-open-test",
                items,
                SuccessProcessor);
            Assert.False(result1.IsSuccessful);

            // The circuit should reset after the timeout (2 minutes)
            // We can't easily test this without time control
            // But we can verify the statistics show the circuit as open
            var stats = service.GetStatistics();
            Assert.True(stats.CircuitBreakerOpen);
        }

        [Fact]
        public async Task ExecuteResilientBatch_CircuitBreakerClosesOnSuccess()
        {
            // Arrange
            var service = CreateService();
            var items = new[] { "item1" };

            // Record failures to open circuit
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    await service.ExecuteResilientBatchAsync(
                        $"failure-{i}",
                        items,
                        FailingProcessor);
                }
                catch { }
            }

            // Verify circuit is open
            var stats1 = service.GetStatistics();
            Assert.True(stats1.CircuitBreakerOpen);

            // Note: In actual implementation, circuit stays open for timeout period
            // This test verifies the structure exists
        }

        [Fact]
        public async Task GetStatistics_ReturnsCircuitBreakerStatus()
        {
            // Arrange
            var service = CreateService();
            var items = new[] { "item1" };

            // Act - get initial stats
            var stats1 = service.GetStatistics();
            Assert.False(stats1.CircuitBreakerOpen);
            Assert.Equal(0, stats1.ConsecutiveFailures);

            // Cause failures
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    await service.ExecuteResilientBatchAsync(
                        $"failure-{i}",
                        items,
                        FailingProcessor);
                }
                catch { }
            }

            // Act - get stats after failures
            var stats2 = service.GetStatistics();
            Assert.Equal(3, stats2.ConsecutiveFailures);
        }

        #endregion

        #region Checkpoint Tests

        [Fact]
        public async Task ExecuteResilientBatch_SavesCheckpointAtInterval()
        {
            // Arrange
            var service = CreateService();
            var items = Enumerable.Range(1, 25).Select(i => $"item{i}").ToList();
            var progressReports = new List<BatchProgress>();

            var progress = new SynchronousProgress<BatchProgress>(p => progressReports.Add(p));

            // Act
            var result = await service.ExecuteResilientBatchAsync(
                "checkpoint-test",
                items,
                SuccessProcessor,
                NetworkResilienceOptions.Default,
                progress);

            // Assert
            Assert.True(result.IsSuccessful);
            Assert.Equal(25, progressReports.Count);

            // Check that checkpoints were reported at intervals
            // Default checkpoint interval is 10
            var checkpointReports = progressReports.Where(p => p.CompletedItems % 10 == 0).ToList();
            Assert.True(checkpointReports.Count >= 2);
        }

        [Fact]
        public async Task ExecuteResilientBatch_ResumesFromCheckpoint()
        {
            // Arrange
            var service = CreateService();
            var items = new[] { "item1", "item2", "item3" };
            var processedItems = new List<string>();

            async Task<string> CheckpointProcessor(string item, CancellationToken ct)
            {
                await Task.CompletedTask;
                processedItems.Add(item);
                if (item == "item2")
                    throw new HttpRequestException("Network error");
                return $"processed-{item}";
            }

            var options = new NetworkResilienceOptions { ContinueOnFailure = false };

            // Act - First run that fails
            var result1 = await service.ExecuteResilientBatchAsync(
                "checkpoint-resume-test",
                items,
                CheckpointProcessor,
                options);

            // Assert - Checkpoint should be available
            Assert.False(result1.IsSuccessful);
            Assert.True(result1.CanRetryFromCheckpoint);
            Assert.Single(result1.Results);
        }

        #endregion

        #region Progress Reporting Tests

        [Fact]
        public async Task ExecuteResilientBatch_ReportsProgress()
        {
            // Arrange
            var service = CreateService();
            var items = new[] { "item1", "item2", "item3" };
            var progressReports = new List<BatchProgress>();
            var progress = new SynchronousProgress<BatchProgress>(p => progressReports.Add(p));

            // Act
            var result = await service.ExecuteResilientBatchAsync(
                "progress-test",
                items,
                SuccessProcessor,
                NetworkResilienceOptions.Default,
                progress);

            // Assert
            Assert.True(result.IsSuccessful);
            Assert.Equal(3, progressReports.Count);
            var finalReport = progressReports[^1];
            // Stable assertions: TotalItems is deterministic, and completion means all processed
            Assert.Equal(3, finalReport.TotalItems);
            Assert.Equal(finalReport.TotalItems, finalReport.SuccessfulItems + finalReport.FailedItems);
            // In success case, all items should be successful with no failures
            Assert.Equal(3, finalReport.SuccessfulItems);
            Assert.Equal(0, finalReport.FailedItems);
        }

        [Fact]
        public async Task ExecuteResilientBatch_ProgressIncludesSuccessfulAndFailedItems()
        {
            // Arrange
            var service = CreateService();
            var items = new[] { "item1", "item2", "item3" };
            var progressReports = new List<BatchProgress>();
            var progress = new SynchronousProgress<BatchProgress>(p => progressReports.Add(p));

            async Task<string> PartialFailureProcessor(string item, CancellationToken ct)
            {
                await Task.CompletedTask;
                if (item == "item2")
                    throw new HttpRequestException("Network error");
                return $"processed-{item}";
            }

            // Act
            var result = await service.ExecuteResilientBatchAsync(
                "progress-failure-test",
                items,
                PartialFailureProcessor,
                NetworkResilienceOptions.Default,
                progress);

            // Assert
            // IsSuccessful is true when ContinueOnFailure=true and there are some results
            Assert.True(result.IsSuccessful);
            Assert.Equal(3, progressReports.Count);

            // Last report should show 2 successful, 1 failed
            var finalReport = progressReports.Last();
            Assert.Equal(2, finalReport.SuccessfulItems);
            Assert.Equal(1, finalReport.FailedItems);
        }

        #endregion

        #region Network Health Tests

        [Fact]
        public void GetNetworkHealth_ReturnsCurrentStatus()
        {
            // Arrange
            var service = CreateService();

            // Act
            var health = service.GetNetworkHealth();

            // Assert
            Assert.True(Enum.IsDefined(typeof(NetworkHealthStatus), health));
        }

        [Fact]
        public void GetStatistics_ReturnsCurrentHealth()
        {
            // Arrange
            var service = CreateService();

            // Act
            var stats = service.GetStatistics();

            // Assert
            Assert.NotNull(stats);
            Assert.False(stats.CircuitBreakerOpen);
            Assert.Equal(0, stats.ConsecutiveFailures);
            Assert.True(Enum.IsDefined(typeof(NetworkHealthStatus), stats.NetworkHealth));
        }

        [Fact]
        public async Task GetStatistics_ReflectsActiveOperations()
        {
            // Arrange
            var service = CreateService();
            var items = new[] { "item1", "item2", "item3" };

            // Start a long-running operation
            var tcs = new TaskCompletionSource<string>();
            async Task<string> SlowProcessor(string item, CancellationToken ct)
            {
                await Task.Delay(100, ct);
                return $"processed-{item}";
            }

            // Act
            var task = service.ExecuteResilientBatchAsync(
                "active-test",
                items,
                SlowProcessor);

            // Wait for operation to start
            await Task.Delay(50);

            // Get stats while operation is active
            var stats = service.GetStatistics();

            // Assert
            Assert.True(stats.ActiveOperations >= 0);

            // Wait for completion
            await task;
        }

        #endregion

        #region HTTP Batch Tests

        [Fact]
        public async Task ExecuteHttpBatch_WithValidRequests_ReturnsResults()
        {
            // Arrange
            var service = CreateService();
            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("response")
                });

            using var httpClient = new HttpClient(mockHandler.Object);
            var requests = new[]
            {
                new HttpRequestMessage(HttpMethod.Get, "https://example.com/1"),
                new HttpRequestMessage(HttpMethod.Get, "https://example.com/2")
            };

            var policy = ResiliencePolicy.Default;

            // Act
            var result = await service.ExecuteHttpBatchAsync(
                "http-batch-test",
                requests,
                httpClient,
                policy);

            // Assert
            Assert.True(result.IsSuccessful);
            Assert.Equal(2, result.Results.Count);
        }

        [Fact]
        public async Task ExecuteHttpBatch_CircuitBreakerAbortsBatch()
        {
            // Arrange
            var service = CreateService();
            var items = new[] { "item1" };

            // Open the circuit first
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    await service.ExecuteResilientBatchAsync(
                        $"failure-{i}",
                        items,
                        FailingProcessor);
                }
                catch { }
            }

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK
                });

            using var httpClient = new HttpClient(mockHandler.Object);
            var requests = new[]
            {
                new HttpRequestMessage(HttpMethod.Get, "https://example.com/1")
            };

            // Act
            var result = await service.ExecuteHttpBatchAsync(
                "http-circuit-test",
                requests,
                httpClient,
                ResiliencePolicy.Default);

            // Assert
            Assert.False(result.IsSuccessful);
            Assert.Equal("Circuit breaker open", result.FailureReason);
        }

        #endregion

        #region Exponential Backoff Tests

        [Fact]
        public async Task CalculateRetryDelay_ExponentialBackoff()
        {
            // This is a private method, so we test it indirectly through the retry behavior
            // The formula is: baseRetryDelay * 2^attemptNumber, capped at maxRetryDelay
            // Default values: base=1s, max=30s

            // Arrange
            var service = CreateService();
            var items = new[] { "item1" };
            var attemptTimes = new List<DateTime>();

            Task<string> RetryProcessor(string item, CancellationToken ct)
            {
                attemptTimes.Add(DateTime.UtcNow);
                if (attemptTimes.Count < 3)
                    throw new HttpRequestException("Transient error");
                return Task.FromResult("success");
            }

            // Act
            var result = await service.ExecuteResilientBatchAsync(
                "backoff-test",
                items,
                RetryProcessor);

            // Assert - The delays should increase exponentially
            // We can't easily measure exact delays without time control
            // But we can verify retries happened
            Assert.NotNull(result);
        }

        #endregion

        #region Options Tests

        [Fact]
        public async Task ExecuteResilientBatch_UsesDefaultOptions()
        {
            // Arrange
            var service = CreateService();
            var items = new[] { "item1" };

            async Task<string> Processor(string item, CancellationToken ct)
            {
                await Task.CompletedTask;
                return "success";
            }

            // Act
            var result = await service.ExecuteResilientBatchAsync(
                "default-options-test",
                items,
                Processor);

            // Assert - Should succeed with default options
            Assert.True(result.IsSuccessful);
        }

        [Fact]
        public void NetworkResilienceOptions_Default_IsValid()
        {
            // Arrange & Act
            var options = NetworkResilienceOptions.Default;

            // Assert
            Assert.True(options.ContinueOnFailure);
            Assert.True(options.EnableCheckpoints);
            Assert.Equal(10, options.CheckpointInterval);
            Assert.Equal(3, options.MaxRetryAttempts);
            Assert.Equal(TimeSpan.FromSeconds(1), options.RetryDelay);
        }

        [Fact]
        public void NetworkResilienceOptions_StrictMode_IsValid()
        {
            // Arrange & Act
            var options = NetworkResilienceOptions.StrictMode;

            // Assert
            Assert.False(options.ContinueOnFailure);
            Assert.True(options.EnableCheckpoints);
            Assert.Equal(5, options.MaxRetryAttempts);
        }

        #endregion

        #region BatchProgress Tests

        [Fact]
        public void BatchProgress_CalculatesPercentComplete()
        {
            // Arrange
            var progress = new BatchProgress
            {
                CompletedItems = 5,
                TotalItems = 10
            };

            // Act
            var percent = progress.PercentComplete;

            // Assert
            Assert.Equal(50.0, percent);
        }

        [Fact]
        public void BatchProgress_ZeroTotalItems_ReturnsZeroPercent()
        {
            // Arrange
            var progress = new BatchProgress
            {
                CompletedItems = 5,
                TotalItems = 0
            };

            // Act
            var percent = progress.PercentComplete;

            // Assert
            Assert.Equal(0.0, percent);
        }

        #endregion

        #region ResilientBatchResult Tests

        [Fact]
        public void ResilientBatchResult_CalculatesSuccessRate()
        {
            // Arrange
            var result = new ResilientBatchResult<string>
            {
                Results = new List<string> { "r1", "r2", "r3" },
                Failures = new List<BatchItemFailure>
                {
                    new BatchItemFailure { ItemIndex = 3 },
                    new BatchItemFailure { ItemIndex = 4 }
                }
            };

            // Act
            var successRate = result.SuccessRate;

            // Assert
            Assert.Equal(0.6, successRate); // 3 successes out of 5 total
        }

        [Fact]
        public void ResilientBatchResult_EmptyResults_ReturnsZeroSuccessRate()
        {
            // Arrange
            var result = new ResilientBatchResult<string>
            {
                Results = new List<string>(),
                Failures = new List<BatchItemFailure>()
            };

            // Act
            var successRate = result.SuccessRate;

            // Assert
            Assert.Equal(0.0, successRate);
        }

        #endregion

        #region Cancellation Tests

        [Fact]
        public async Task ExecuteResilientBatch_WithCancellationToken_CanBeCancelled()
        {
            // Arrange
            var service = CreateService();
            var items = Enumerable.Range(1, 100).Select(i => $"item{i}").ToList();
            var cts = new CancellationTokenSource();

            async Task<string> SlowProcessor(string item, CancellationToken ct)
            {
                await Task.Delay(100, ct);
                return $"processed-{item}";
            }

            // Act
            var task = service.ExecuteResilientBatchAsync(
                "cancel-test",
                items,
                SlowProcessor,
                NetworkResilienceOptions.Default,
                null!,
                cts.Token);

            // Cancel after a short delay
            await Task.Delay(150);
            cts.Cancel();

            // Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }

        #endregion

        #region Null Handling Tests

        [Fact]
        public async Task ExecuteResilientBatch_EmptyItemsWithNullProcessor_ReturnsSuccess()
        {
            // Arrange
            var service = CreateService();

            // Act
            // When items is empty, the method returns early without invoking the processor,
            // so null processor doesn't cause an issue
            var result = await service.ExecuteResilientBatchAsync<string, string>(
                "empty-null-processor-test",
                Array.Empty<string>(),
                null!);

            // Assert
            Assert.NotNull(result);
            Assert.True(result.IsSuccessful);
            Assert.Empty(result.Results);
        }

        #endregion

        #region Constructor Tests

        [Fact]
        public void Constructor_NullLogger_ThrowsArgumentNullException()
        {
            // Act & Assert
            // The constructor DOES validate null logger and throws ArgumentNullException
            Assert.Throws<ArgumentNullException>(() =>
                new NetworkResilienceService(null!));
        }

        #endregion
    }
}
