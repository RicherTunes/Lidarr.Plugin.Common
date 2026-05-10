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
    [Trait("Category", "Coverage")]
    public class NetworkResilienceServiceCovTests2
    {
        private NetworkResilienceService CreateService()
        {
            return new NetworkResilienceService(Mock.Of<ILogger<NetworkResilienceService>>());
        }

        private class SyncProgress<T> : IProgress<T>
        {
            private readonly Action<T> _handler;
            public SyncProgress(Action<T> handler) => _handler = handler;
            public void Report(T value) => _handler(value);
        }

        /// <summary>
        /// Source lines 93-100: Circuit breaker check in ExecuteHttpBatchAsync
        /// Verifies circuit breaker aborts HTTP batch with correct result.
        /// </summary>
        [Fact]
        public async Task ExecuteHttpBatchAsync_CircuitBreakerOpen_AbortsBatch()
        {
            var service = CreateService();

            // Open circuit by causing 5 failures via HTTP batch
            for (int i = 0; i < 5; i++)
            {
                var failHandler = new Mock<HttpMessageHandler>();
                failHandler.Protected()
                    .Setup<Task<HttpResponseMessage>>(
                        "SendAsync",
                        ItExpr.IsAny<HttpRequestMessage>(),
                        ItExpr.IsAny<CancellationToken>())
                    .Returns(() => Task.FromException<HttpResponseMessage>(
                        new HttpRequestException("err")));

                using var failClient = new HttpClient(failHandler.Object);
                var failReqs = new[] { new HttpRequestMessage(HttpMethod.Get, "https://x.com/1") };

                await service.ExecuteHttpBatchAsync(
                    $"circuit-fail-{i}",
                    failReqs,
                    failClient,
                    ResiliencePolicy.Default);
            }

            // Now circuit is open - verify it aborts
            var goodHandler = new Mock<HttpMessageHandler>();
            goodHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage { StatusCode = HttpStatusCode.OK });

            using var goodClient = new HttpClient(goodHandler.Object);
            var goodReqs = new[]
            {
                new HttpRequestMessage(HttpMethod.Get, "https://x.com/1"),
                new HttpRequestMessage(HttpMethod.Get, "https://x.com/2"),
            };

            var result = await service.ExecuteHttpBatchAsync(
                "after-circuit",
                goodReqs,
                goodClient,
                ResiliencePolicy.Default);

            Assert.False(result.IsSuccessful);
            Assert.Equal("Circuit breaker open", result.FailureReason);
            Assert.True(result.CanRetryLater);
            Assert.Empty(result.Results);
        }

        /// <summary>
        /// Source lines 110-119: HTTP batch catch block with RecordFailure
        /// Verifies failures are recorded with correct item index and CanRetry flag.
        /// </summary>
        [Fact]
        public async Task ExecuteHttpBatchAsync_Failure_RecordsFailureWithCorrectDetails()
        {
            var service = CreateService();
            var handler = new Mock<HttpMessageHandler>();
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Returns(() => Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("network error")));

            using var client = new HttpClient(handler.Object);
            var reqs = new[]
            {
                new HttpRequestMessage(HttpMethod.Get, "https://x.com/1"),
                new HttpRequestMessage(HttpMethod.Get, "https://x.com/2"),
            };

            var result = await service.ExecuteHttpBatchAsync(
                "failure-test",
                reqs,
                client,
                ResiliencePolicy.Default);

            Assert.False(result.IsSuccessful);
            Assert.Equal(2, result.Failures.Count);
            Assert.Equal(0, result.Failures[0].ItemIndex);
            Assert.Equal(1, result.Failures[1].ItemIndex);
            Assert.False(result.Failures[0].CanRetry);
            Assert.IsType<HttpRequestException>(result.Failures[0].Error);
        }

        /// <summary>
        /// Source lines 107-108: RecordSuccess after successful HTTP request
        /// Verifies successful HTTP batch operations reset failure count.
        /// </summary>
        [Fact]
        public async Task ExecuteHttpBatchAsync_Success_ResetsFailureCount()
        {
            var service = CreateService();

            // Cause one failure
            var failHandler = new Mock<HttpMessageHandler>();
            failHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Returns(() => Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("err")));

            using var failClient = new HttpClient(failHandler.Object);
            await service.ExecuteHttpBatchAsync(
                "pre-fail",
                new[] { new HttpRequestMessage(HttpMethod.Get, "https://x.com/1") },
                failClient,
                ResiliencePolicy.Default);

            var statsAfterFail = service.GetStatistics();
            Assert.Equal(1, statsAfterFail.ConsecutiveFailures);

            // Now succeed - should reset failure count
            var successHandler = new Mock<HttpMessageHandler>();
            successHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage { StatusCode = HttpStatusCode.OK });

            using var successClient = new HttpClient(successHandler.Object);
            await service.ExecuteHttpBatchAsync(
                "success",
                new[] { new HttpRequestMessage(HttpMethod.Get, "https://x.com/1") },
                successClient,
                ResiliencePolicy.Default);

            var statsAfterSuccess = service.GetStatistics();
            Assert.Equal(0, statsAfterSuccess.ConsecutiveFailures);
        }

        /// <summary>
        /// Source lines 432-436: RecordSuccess resets consecutive failures when > 0
        /// </summary>
        [Fact]
        public async Task ExecuteResilientBatch_RecordSuccessResetsConsecutiveFailures()
        {
            var service = CreateService();

            async Task<string> AlwaysFailProcessor(string item, CancellationToken ct)
            {
                await Task.CompletedTask;
                throw new HttpRequestException("Always fails");
            }

            var result1 = await service.ExecuteResilientBatchAsync(
                "fail-reset-test",
                new[] { "item1" },
                AlwaysFailProcessor);

            Assert.False(result1.IsSuccessful);
            var statsAfterFail = service.GetStatistics();
            Assert.Equal(1, statsAfterFail.ConsecutiveFailures);

            var attemptCount = 0;
            async Task<string> EventualSuccessProcessor(string item, CancellationToken ct)
            {
                await Task.CompletedTask;
                attemptCount++;
                if (attemptCount <= 2)
                    throw new HttpRequestException("Transient error");
                return "success";
            }

            var result2 = await service.ExecuteResilientBatchAsync(
                "success-reset-test",
                new[] { "item1" },
                EventualSuccessProcessor);

            Assert.True(result2.IsSuccessful);
            var statsAfterSuccess = service.GetStatistics();
            Assert.Equal(0, statsAfterSuccess.ConsecutiveFailures);
        }

        /// <summary>
        /// Source line 394: exception is TimeoutException
        /// Verifies TimeoutException is retryable via ShouldRetry.
        /// </summary>
        [Fact]
        public async Task ExecuteResilientBatch_TimeoutException_IsRetryable()
        {
            var service = CreateService();
            var attempts = 0;
            async Task<string> TimeoutExProcessor(string item, CancellationToken ct)
            {
                await Task.CompletedTask;
                attempts++;
                throw new TimeoutException("Request timed out");
            }

            var result = await service.ExecuteResilientBatchAsync(
                "timeout-ex-test",
                new[] { "x" },
                TimeoutExProcessor);

            Assert.False(result.IsSuccessful);
            Assert.Equal(3, attempts);
            Assert.Single(result.Failures);
            Assert.True(result.Failures[0].CanRetry);
        }

        /// <summary>
        /// Source line 395: exception is HttpRequestException
        /// Verifies HttpRequestException is retryable via ShouldRetry.
        /// </summary>
        [Fact]
        public async Task ExecuteResilientBatch_HttpRequestException_IsRetryable()
        {
            var service = CreateService();
            var attempts = 0;
            async Task<string> HttpExProcessor(string item, CancellationToken ct)
            {
                await Task.CompletedTask;
                attempts++;
                throw new HttpRequestException("Network error");
            }

            var result = await service.ExecuteResilientBatchAsync(
                "http-ex-test",
                new[] { "x" },
                HttpExProcessor);

            Assert.False(result.IsSuccessful);
            Assert.Equal(3, attempts);
            Assert.Single(result.Failures);
            Assert.True(result.Failures[0].CanRetry);
        }

        /// <summary>
        /// Source line 397: TaskCanceledException WITHOUT "A task was canceled" message IS retryable.
        /// </summary>
        [Fact]
        public async Task ExecuteResilientBatch_TaskCanceledExceptionWithoutCancelMessage_IsRetryable()
        {
            var service = CreateService();
            var attempts = 0;
            async Task<string> TimeoutTceProcessor(string item, CancellationToken ct)
            {
                await Task.CompletedTask;
                attempts++;
                throw new TaskCanceledException("The operation timed out due to network latency");
            }

            var result = await service.ExecuteResilientBatchAsync(
                "tce-retry-test",
                new[] { "item1" },
                TimeoutTceProcessor);

            Assert.False(result.IsSuccessful);
            Assert.Equal(3, attempts);
            Assert.Single(result.Failures);
            Assert.True(result.Failures[0].CanRetry);
        }

        /// <summary>
        /// Source line 397: TaskCanceledException WITH "A task was canceled" message is NOT retryable.
        /// </summary>
        [Fact]
        public async Task ExecuteResilientBatch_TaskCanceledExceptionWithCancelMessage_NotRetryable()
        {
            var service = CreateService();
            var attempts = 0;
            async Task<string> CancelTceProcessor(string item, CancellationToken ct)
            {
                await Task.CompletedTask;
                attempts++;
                throw new TaskCanceledException("A task was canceled");
            }

            var result = await service.ExecuteResilientBatchAsync(
                "tce-no-retry-test",
                new[] { "item1" },
                CancelTceProcessor);

            Assert.False(result.IsSuccessful);
            Assert.Equal(1, attempts);
            Assert.Single(result.Failures);
            Assert.False(result.Failures[0].CanRetry);
        }

        /// <summary>
        /// Source lines 307-308: IsSuccessful when all items fail with ContinueOnFailure=true
        /// When results.Any() is false, IsSuccessful should be false.
        /// </summary>
        [Fact]
        public async Task ExecuteResilientBatch_AllItemsFail_ContinueOnFailure_IsNotSuccessful()
        {
            var service = CreateService();
            async Task<string> FailProcessor(string item, CancellationToken ct)
            {
                await Task.CompletedTask;
                throw new InvalidOperationException("Non-retryable failure");
            }

            var result = await service.ExecuteResilientBatchAsync(
                "all-fail-test",
                new[] { "item1", "item2", "item3" },
                FailProcessor,
                NetworkResilienceOptions.Default);

            Assert.False(result.IsSuccessful);
            Assert.Empty(result.Results);
            Assert.Equal(3, result.Failures.Count);
        }

        /// <summary>
        /// Source lines 530-539: GetStatistics returns all fields
        /// </summary>
        [Fact]
        public void GetStatistics_InitialState_ReturnsCorrectDefaults()
        {
            var service = CreateService();
            var stats = service.GetStatistics();

            Assert.Equal(0, stats.ActiveOperations);
            Assert.Equal(0, stats.ConsecutiveFailures);
            Assert.False(stats.CircuitBreakerOpen);
            Assert.Equal(DateTime.MinValue, stats.LastFailureTime);
            Assert.Equal(NetworkHealthStatus.Healthy, stats.NetworkHealth);
        }

        /// <summary>
        /// Source lines 442-450: RecordFailure increments and logs warning at threshold
        /// Tests that after exactly 5 consecutive failures, circuit breaker opens.
        /// </summary>
        [Fact]
        public async Task ExecuteResilientBatch_CircuitBreakerOpensAtExactThreshold()
        {
            var service = CreateService();

            async Task<string> FailProcessor(string item, CancellationToken ct)
            {
                await Task.CompletedTask;
                throw new HttpRequestException("Network error");
            }

            for (int i = 0; i < 5; i++)
            {
                var result = await service.ExecuteResilientBatchAsync(
                    $"threshold-fail-{i}",
                    new[] { "item1" },
                    FailProcessor);
                Assert.False(result.IsSuccessful);
            }

            var stats = service.GetStatistics();
            Assert.True(stats.CircuitBreakerOpen);
            Assert.Equal(5, stats.ConsecutiveFailures);

            Task<string> NoExecuteProcessor(string item, CancellationToken ct) =>
                Task.FromResult("should not execute");
            var result2 = await service.ExecuteResilientBatchAsync(
                "after-circuit-open",
                new[] { "item1" },
                NoExecuteProcessor);

            Assert.False(result2.IsSuccessful);
            Assert.Contains("circuit", result2.FailureReason, StringComparison.OrdinalIgnoreCase);
            Assert.True(result2.CanRetryLater);
        }

        /// <summary>
        /// Source lines 262-281: Handle failure with ContinueOnFailure=false, save checkpoint
        /// </summary>
        [Fact]
        public async Task ExecuteResilientBatch_StopOnFailure_SavesCheckpointAndCanRetry()
        {
            var service = CreateService();
            var processedItems = new List<string>();

            async Task<string> FailingOnSecondProcessor(string item, CancellationToken ct)
            {
                await Task.CompletedTask;
                processedItems.Add(item);
                if (item == "item2")
                    throw new HttpRequestException("Network error");
                return $"processed-{item}";
            }

            var result = await service.ExecuteResilientBatchAsync(
                "checkpoint-fail-test",
                new[] { "item1", "item2", "item3" },
                FailingOnSecondProcessor,
                new NetworkResilienceOptions { ContinueOnFailure = false });

            Assert.False(result.IsSuccessful);
            Assert.True(result.CanRetryFromCheckpoint);
            Assert.Single(result.Results);
            Assert.Equal("processed-item1", result.Results[0]);
        }

        /// <summary>
        /// Source lines 340-349: Retry success logging on attempt > 0
        /// </summary>
        [Fact]
        public async Task ExecuteResilientBatch_RetrySucceeds_ReturnsSuccessfulResult()
        {
            var service = CreateService();
            var attemptCount = 0;

            async Task<string> EventualSuccessProcessor(string item, CancellationToken ct)
            {
                await Task.CompletedTask;
                attemptCount++;
                if (attemptCount < 3)
                    throw new HttpRequestException("Transient error");
                return "success";
            }

            var result = await service.ExecuteResilientBatchAsync(
                "retry-success-test",
                new[] { "item1" },
                EventualSuccessProcessor);

            Assert.True(result.IsSuccessful);
            Assert.Equal(3, attemptCount);
            Assert.Single(result.Results);
            Assert.Equal("success", result.Results[0]);
        }

        /// <summary>
        /// Source lines 285-293: Checkpoint at interval
        /// </summary>
        [Fact]
        public async Task ExecuteResilientBatch_SavesCheckpointsAtInterval()
        {
            var service = CreateService();
            var items = new List<string>();
            for (int i = 0; i < 25; i++)
            {
                items.Add($"item{i}");
            }

            var result = await service.ExecuteResilientBatchAsync(
                "checkpoint-interval-test",
                items,
                async (item, ct) =>
                {
                    await Task.CompletedTask;
                    return $"processed-{item}";
                },
                NetworkResilienceOptions.Default);

            Assert.True(result.IsSuccessful);
            Assert.Equal(25, result.Results.Count);
        }

        /// <summary>
        /// Source line 635: BatchProgress.PercentComplete calculation
        /// </summary>
        [Fact]
        public void BatchProgress_PercentComplete_CalculatesCorrectly()
        {
            var progress = new BatchProgress
            {
                CompletedItems = 5,
                TotalItems = 10
            };

            Assert.Equal(50.0, progress.PercentComplete);
        }

        /// <summary>
        /// Source line 635: BatchProgress.PercentComplete with zero total
        /// </summary>
        [Fact]
        public void BatchProgress_ZeroTotal_ReturnsZeroPercent()
        {
            var progress = new BatchProgress
            {
                CompletedItems = 5,
                TotalItems = 0
            };

            Assert.Equal(0.0, progress.PercentComplete);
        }

        /// <summary>
        /// Source lines 652-653: ResilientBatchResult.SuccessRate calculation
        /// </summary>
        [Fact]
        public void ResilientBatchResult_SuccessRate_CalculatesCorrectly()
        {
            var result = new ResilientBatchResult<string>
            {
                Results = new List<string> { "a", "b", "c" },
                Failures = new List<BatchItemFailure>
                {
                    new BatchItemFailure { ItemIndex = 0 },
                    new BatchItemFailure { ItemIndex = 1 }
                }
            };

            // 3 success / (3 + 2) total = 0.6
            Assert.Equal(0.6, result.SuccessRate);
        }

        /// <summary>
        /// Source lines 652-653: ResilientBatchResult.SuccessRate with empty results
        /// </summary>
        [Fact]
        public void ResilientBatchResult_Empty_ReturnsZeroSuccessRate()
        {
            var result = new ResilientBatchResult<string>
            {
                Results = new List<string>(),
                Failures = new List<BatchItemFailure>()
            };

            Assert.Equal(0.0, result.SuccessRate);
        }

        /// <summary>
        /// Source lines 584-600: NetworkResilienceOptions default and strict mode
        /// </summary>
        [Fact]
        public void NetworkResilienceOptions_Default_HasCorrectValues()
        {
            var options = NetworkResilienceOptions.Default;

            Assert.True(options.ContinueOnFailure);
            Assert.True(options.EnableCheckpoints);
            Assert.Equal(10, options.CheckpointInterval);
            Assert.Equal(3, options.MaxRetryAttempts);
            Assert.Equal(TimeSpan.FromSeconds(1), options.RetryDelay);
        }

        /// <summary>
        /// Source lines 594-599: NetworkResilienceOptions.StrictMode
        /// </summary>
        [Fact]
        public void NetworkResilienceOptions_StrictMode_HasCorrectValues()
        {
            var options = NetworkResilienceOptions.StrictMode;

            Assert.False(options.ContinueOnFailure);
            Assert.True(options.EnableCheckpoints);
            Assert.Equal(5, options.MaxRetryAttempts);
        }

        /// <summary>
        /// Source line 91: cancellationToken.ThrowIfCancellationRequested();
        /// Tests HTTP batch cancellation via cancellation token.
        /// </summary>
        [Fact]
        public async Task ExecuteHttpBatchAsync_WithCancellation_ThrowsOperationCanceledException()
        {
            var service = CreateService();
            var handler = new Mock<HttpMessageHandler>();
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Returns(async (HttpRequestMessage req, CancellationToken ct) =>
                {
                    await Task.Delay(50, ct);
                    return new HttpResponseMessage { StatusCode = HttpStatusCode.OK };
                });

            using var client = new HttpClient(handler.Object);
            var reqs = new[]
            {
                new HttpRequestMessage(HttpMethod.Get, "https://x.com/1"),
                new HttpRequestMessage(HttpMethod.Get, "https://x.com/2"),
                new HttpRequestMessage(HttpMethod.Get, "https://x.com/3"),
            };

            var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(30));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ExecuteHttpBatchAsync(
                    "cancel-test",
                    reqs,
                    client,
                    ResiliencePolicy.Default,
                    NetworkResilienceOptions.Default,
                    null!,
                    cts.Token));
        }

        /// <summary>
        /// Source lines 619-621: BatchItemFailure defaults
        /// </summary>
        [Fact]
        public void BatchItemFailure_DefaultFailureTime_IsUtcNow()
        {
            var before = DateTime.UtcNow;
            var failure = new BatchItemFailure
            {
                ItemIndex = 0,
                Error = new Exception("test"),
                CanRetry = true
            };
            var after = DateTime.UtcNow;

            Assert.True(failure.FailureTime >= before);
            Assert.True(failure.FailureTime <= after);
        }

        /// <summary>
        /// Source lines 617-621: BatchItemFailure properties
        /// </summary>
        [Fact]
        public void BatchItemFailure_PropertiesAreSetCorrectly()
        {
            var error = new InvalidOperationException("test error");
            var failure = new BatchItemFailure
            {
                ItemIndex = 5,
                Error = error,
                CanRetry = true
            };

            Assert.Equal(5, failure.ItemIndex);
            Assert.Same(error, failure.Error);
            Assert.True(failure.CanRetry);
        }

        /// <summary>
        /// Source lines 627-635: BatchProgress properties
        /// </summary>
        [Fact]
        public void BatchProgress_PropertiesAreSetCorrectly()
        {
            var progress = new BatchProgress
            {
                CompletedItems = 10,
                TotalItems = 20,
                SuccessfulItems = 8,
                FailedItems = 2,
                CurrentItem = 10
            };

            Assert.Equal(10, progress.CompletedItems);
            Assert.Equal(20, progress.TotalItems);
            Assert.Equal(8, progress.SuccessfulItems);
            Assert.Equal(2, progress.FailedItems);
            Assert.Equal(10, progress.CurrentItem);
            Assert.Equal(50.0, progress.PercentComplete);
        }

        /// <summary>
        /// Source lines 641-653: ResilientBatchResult properties
        /// </summary>
        [Fact]
        public void ResilientBatchResult_Defaults_AreCorrect()
        {
            var result = new ResilientBatchResult<string>();

            Assert.Empty(result.Results);
            Assert.Empty(result.Failures);
            Assert.Equal(TimeSpan.Zero, result.CompletionTime);
            Assert.Equal(0.0, result.SuccessRate);
        }

        /// <summary>
        /// Source lines 669-676: NetworkResilienceStatistics properties
        /// </summary>
        [Fact]
        public void NetworkResilienceStatistics_PropertiesAreSetCorrectly()
        {
            var stats = new NetworkResilienceStatistics
            {
                ActiveOperations = 3,
                ConsecutiveFailures = 2,
                CircuitBreakerOpen = true,
                LastFailureTime = DateTime.UtcNow,
                NetworkHealth = NetworkHealthStatus.Degraded
            };

            Assert.Equal(3, stats.ActiveOperations);
            Assert.Equal(2, stats.ConsecutiveFailures);
            Assert.True(stats.CircuitBreakerOpen);
            Assert.Equal(NetworkHealthStatus.Degraded, stats.NetworkHealth);
        }

        /// <summary>
        /// Source lines 569-579: BatchOperationState properties
        /// </summary>
        [Fact]
        public void BatchOperationState_Defaults_AreCorrect()
        {
            var state = new BatchOperationState();

            Assert.Null(state.OperationId);
            Assert.Equal(0, state.TotalItems);
            Assert.Equal(0, state.CompletedItems);
            Assert.Equal(default, state.StartTime);
            Assert.Equal(default, state.LastCheckpointTime);
            Assert.Empty(state.Results);
            Assert.Empty(state.Failures);
            Assert.Null(state.Options);
        }

        /// <summary>
        /// Source lines 605-611: ItemProcessingResult properties
        /// </summary>
        [Fact]
        public void ItemProcessingResult_Defaults_AreCorrect()
        {
            var result = new ItemProcessingResult<string>();

            Assert.False(result.IsSuccess);
            Assert.Null(result.Result);
            Assert.Null(result.Exception);
            Assert.False(result.CanRetry);
        }

        /// <summary>
        /// Source lines 554-564: NetworkHealthMonitor constructor and default health
        /// </summary>
        [Fact]
        public void NetworkHealthMonitor_Default_ReturnsHealthy()
        {
            var logger = Mock.Of<ILogger<NetworkResilienceService>>();
            var monitor = new NetworkHealthMonitor(logger);

            var health = monitor.GetCurrentHealth();

            Assert.Equal(NetworkHealthStatus.Healthy, health);
        }

        /// <summary>
        /// Source lines 659-665: NetworkHealthStatus enum values
        /// </summary>
        [Fact]
        public void NetworkHealthStatus_HasExpectedValues()
        {
            Assert.Equal(0, (int)NetworkHealthStatus.Healthy);
            Assert.Equal(1, (int)NetworkHealthStatus.Degraded);
            Assert.Equal(2, (int)NetworkHealthStatus.Unhealthy);
            Assert.Equal(3, (int)NetworkHealthStatus.Unknown);
        }

        /// <summary>
        /// Source line 223: cancellationToken.ThrowIfCancellationRequested() in ExecuteBatchWithRecoveryAsync
        /// Tests cancellation in resilient batch.
        /// </summary>
        [Fact]
        public async Task ExecuteResilientBatch_Cancellation_ThrowsOperationCanceledException()
        {
            var service = CreateService();
            var cts = new CancellationTokenSource();

            async Task<string> SlowProcessor(string item, CancellationToken ct)
            {
                await Task.Delay(100, ct);
                return $"processed-{item}";
            }

            var task = service.ExecuteResilientBatchAsync(
                "cancel-test",
                Enumerable.Range(1, 100).Select(i => $"item{i}").ToList(),
                SlowProcessor,
                NetworkResilienceOptions.Default,
                null!,
                cts.Token);

            cts.CancelAfter(TimeSpan.FromMilliseconds(150));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }

        /// <summary>
        /// Source line 278: FailureReason = itemResult.Exception?.Message ?? "Item processing failed"
        /// Tests the null-coalescing path when exception message is null.
        /// </summary>
        [Fact]
        public async Task ExecuteResilientBatch_FailureWithNullExceptionMessage_UsesDefaultMessage()
        {
            var service = CreateService();

            async Task<string> NullMessageExceptionProcessor(string item, CancellationToken ct)
            {
                await Task.CompletedTask;
                throw new NullMessageException();
            }

            var result = await service.ExecuteResilientBatchAsync(
                "null-message-test",
                new[] { "item1" },
                NullMessageExceptionProcessor,
                new NetworkResilienceOptions { ContinueOnFailure = false });

            Assert.False(result.IsSuccessful);
            Assert.Equal("Item processing failed", result.FailureReason);
        }

        /// <summary>
        /// Source lines 296-303: Progress reporting in ExecuteBatchWithRecoveryAsync
        /// Tests that progress includes correct CurrentItem (1-indexed).
        /// </summary>
        [Fact]
        public async Task ExecuteResilientBatch_ProgressReportsCurrentItem()
        {
            var service = CreateService();
            var items = new[] { "item1", "item2", "item3" };
            var reports = new List<BatchProgress>();
            var progress = new SyncProgress<BatchProgress>(p => reports.Add(p));

            var result = await service.ExecuteResilientBatchAsync(
                "current-item-test",
                items,
                async (item, ct) =>
                {
                    await Task.CompletedTask;
                    return $"processed-{item}";
                },
                NetworkResilienceOptions.Default,
                progress);

            Assert.True(result.IsSuccessful);
            Assert.Equal(3, reports.Count);
            Assert.Equal(1, reports[0].CurrentItem);
            Assert.Equal(2, reports[1].CurrentItem);
            Assert.Equal(3, reports[2].CurrentItem);
        }

        /// <summary>
        /// Source lines 56: throw new ArgumentNullException(nameof(logger))
        /// Tests constructor null validation.
        /// </summary>
        [Fact]
        public void Constructor_NullLogger_ThrowsArgumentNullException()
        {
            var ex = Assert.Throws<ArgumentNullException>(() =>
                new NetworkResilienceService(null!));
            Assert.Equal("logger", ex.ParamName);
        }

        /// <summary>
        /// Source lines 364-371: Retry delay calculation and Task.Delay
        /// Tests that retries include delays (indirectly tested through timing).
        /// </summary>
        [Fact]
        public async Task ExecuteResilientBatch_RetriesIncludeExponentialBackoff()
        {
            var service = CreateService();
            var attemptCount = 0;

            async Task<string> TrackAttemptsProcessor(string item, CancellationToken ct)
            {
                await Task.CompletedTask;
                attemptCount++;
                if (attemptCount < 3)
                    throw new HttpRequestException("Transient error");
                return "success";
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var result = await service.ExecuteResilientBatchAsync(
                "backoff-timing-test",
                new[] { "item1" },
                TrackAttemptsProcessor);

            stopwatch.Stop();

            Assert.True(result.IsSuccessful);
            Assert.Equal(3, attemptCount);
            // With exponential backoff (1s base): attempt 1 fails, wait ~1s, attempt 2 fails, wait ~2s, attempt 3 succeeds
            Assert.True(stopwatch.ElapsedMilliseconds >= 2500, $"Expected >= 2500ms, got {stopwatch.ElapsedMilliseconds}ms");
        }

        /// <summary>
        /// Source lines 107-108: Multiple successful HTTP requests maintain zero failure count.
        /// </summary>
        [Fact]
        public async Task ExecuteHttpBatchAsync_MultipleSuccesses_MaintainsZeroFailureCount()
        {
            var service = CreateService();
            var handler = new Mock<HttpMessageHandler>();
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage { StatusCode = HttpStatusCode.OK });

            using var client = new HttpClient(handler.Object);
            var reqs = new[]
            {
                new HttpRequestMessage(HttpMethod.Get, "https://x.com/1"),
                new HttpRequestMessage(HttpMethod.Get, "https://x.com/2"),
                new HttpRequestMessage(HttpMethod.Get, "https://x.com/3"),
            };

            var result = await service.ExecuteHttpBatchAsync(
                "multi-success-test",
                reqs,
                client,
                ResiliencePolicy.Default);

            Assert.True(result.IsSuccessful);
            Assert.Equal(3, result.Results.Count);

            var stats = service.GetStatistics();
            Assert.Equal(0, stats.ConsecutiveFailures);
        }

        /// <summary>
        /// Test helper exception that returns a null Message so the null-coalescing path
        /// in NetworkResilienceService (FailureReason = exception?.Message ?? "Item processing failed")
        /// is exercised. Cannot be achieved via stock exception ctors (Exception.Message returns
        /// a default string when the underlying field is null).
        /// </summary>
        private sealed class NullMessageException : Exception
        {
            public override string Message => null!;
        }
    }
}
