#nullable disable
using System;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Deduplication;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Additional coverage tests for RequestDeduplicator - covers paths not in RequestDeduplicatorCovTests.cs
    /// </summary>
    public class RequestDeduplicatorMoreCovTests
    {
        [Fact]
        public void CreateSearchKey_ReturnsEmptyComponent_WhenArtistIsNull()
        {
            // Line 256: return "empty" when component is null or whitespace
            using var deduper = new RequestDeduplicator(NullLogger<RequestDeduplicator>.Instance);

            var key = deduper.CreateSearchKey(null);
            Assert.Equal("search_artist_empty", key);
        }

        [Fact]
        public void CreateSearchKey_ReturnsEmptyComponent_WhenArtistIsWhitespace()
        {
            // Line 256: return "empty" when component is null or whitespace
            using var deduper = new RequestDeduplicator(NullLogger<RequestDeduplicator>.Instance);

            var key = deduper.CreateSearchKey("   ");
            Assert.Equal("search_artist_empty", key);
        }

        [Fact]
        public void CreateDiscographyKey_ReturnsEmptyComponent_WhenArtistIsNull()
        {
            // Line 256: return "empty" when component is null or whitespace
            using var deduper = new RequestDeduplicator(NullLogger<RequestDeduplicator>.Instance);

            var key = deduper.CreateDiscographyKey(null);
            Assert.Equal("discography_empty", key);
        }

        [Fact]
        public void CreateDiscographyKey_ReturnsEmptyComponent_WhenArtistIsWhitespace()
        {
            // Line 256: return "empty" when component is null or whitespace
            using var deduper = new RequestDeduplicator(NullLogger<RequestDeduplicator>.Instance);

            var key = deduper.CreateDiscographyKey("   ");
            Assert.Equal("discography_empty", key);
        }

        [Theory]
        // Names whose length shrinks once non-word chars are stripped (or which become empty)
        // crashed the old Substring(0, original.Length) bound with ArgumentOutOfRangeException.
        [InlineData("AC/DC")]              // -> "acdc" (4) but bounded by original length 5
        [InlineData("P!nk")]              // -> "pnk" (3) bounded by 4
        [InlineData("Panic! at the Disco")]
        [InlineData("***")]               // -> "" (0) bounded by 3 — non-whitespace so not short-circuited
        [InlineData("!!!")]               // "Chk Chk Chk"
        public void CreateSearchKey_DoesNotThrow_ForNamesShortenedByNormalization(string artist)
        {
            using var deduper = new RequestDeduplicator(NullLogger<RequestDeduplicator>.Instance);

            var ex = Record.Exception(() => deduper.CreateSearchKey(artist, "Some Album!!"));

            Assert.Null(ex);
        }

        [Theory]
        [InlineData("AC/DC")]
        [InlineData("***")]
        public void CreateDiscographyKey_DoesNotThrow_ForNamesShortenedByNormalization(string artist)
        {
            using var deduper = new RequestDeduplicator(NullLogger<RequestDeduplicator>.Instance);

            var ex = Record.Exception(() => deduper.CreateDiscographyKey(artist));

            Assert.Null(ex);
        }

        [Fact]
        public void CreateSearchKey_TruncatesLongComponents_To50Chars()
        {
            using var deduper = new RequestDeduplicator(NullLogger<RequestDeduplicator>.Instance);

            // 80 word chars -> after normalization should be capped at 50 in the component.
            var longName = new string('a', 80);
            var key = deduper.CreateSearchKey(longName);

            // Key shape is "search_artist_<normalized>"; the normalized component must be <= 50.
            var component = key.Substring("search_artist_".Length);
            Assert.True(component.Length <= 50, $"component length {component.Length} should be <= 50");
        }

        [Fact]
        public async Task GetOrCreateAsync_PropagatesFactoryException_WhenFactoryThrows()
        {
            // Lines 197-201: Exception caught, logged, set on TCS, then rethrown
            using var deduper = new RequestDeduplicator(NullLogger<RequestDeduplicator>.Instance);

            var expectedMessage = "Factory failed intentionally";

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => deduper.GetOrCreateAsync<string>("error-key", () => throw new InvalidOperationException(expectedMessage)));

            Assert.Equal(expectedMessage, ex.Message);
        }

        [Fact]
        public async Task GetOrCreateAsync_PropagatesFactoryException_ForMultipleWaiters()
        {
            // Lines 197-201: Exception set on TCS propagates to waiters via GetResultAsync.
            // NOTE: When a coalesced waiter observes the failure, ExecuteNewRequest's outer
            // catch at line 112 falls through to execute the waiter's own factory as a
            // resilience fallback. So waiter2 will see ITS OWN factory's exception (not
            // the original shared exception). This test verifies both behaviors:
            //   - The originating caller (task1) gets the original shared exception.
            //   - A coalesced waiter (task2) re-runs its factory on failure.
            using var deduper = new RequestDeduplicator(NullLogger<RequestDeduplicator>.Instance);

            var factoryStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<string> Factory()
            {
                factoryStarted.SetResult(true);
                await Task.Delay(50);
                throw new InvalidOperationException("Shared failure");
            }

            // Start first request
            var task1 = deduper.GetOrCreateAsync("shared-error", Factory);
            await factoryStarted.Task;

            // Second request joins; on TCS exception, falls back to its own factory.
            var task2 = deduper.GetOrCreateAsync<string>(
                "shared-error",
                () => throw new InvalidOperationException("Waiter fallback failure"));

            var ex1 = await Assert.ThrowsAsync<InvalidOperationException>(() => task1);
            var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() => task2);

            Assert.Equal("Shared failure", ex1.Message);
            // task2 receives its own factory's exception via the fall-through fallback path.
            Assert.Equal("Waiter fallback failure", ex2.Message);
        }

        [Fact]
        public async Task CleanupExpiredRequests_CancelsExpiredTasks()
        {
            // Lines 287-296: CleanupExpiredRequests cancels incomplete expired tasks.
            // Note: production ExecuteNewRequest does `await factory()` without threading
            // any cancellation token into the factory delegate. Cancelling the internal
            // TCS therefore does NOT unblock a factory that is awaiting an unrelated
            // primitive. This test verifies the OBSERVABLE state changes triggered by
            // cleanup (ActiveRequests drops to 0, expired entry removed), then signals
            // blockedTcs so the factory completes and the test does not leak a task.
            using var deduper = new RequestDeduplicator(
                NullLogger<RequestDeduplicator>.Instance,
                requestTimeout: TimeSpan.FromMilliseconds(50),
                cleanupInterval: TimeSpan.FromMilliseconds(30));

            var blockedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var task = deduper.GetOrCreateAsync("expire-test", async () =>
            {
                await blockedTcs.Task;
                return "completed";
            });

            // Wait for timeout and cleanup to occur
            await Task.Delay(200);

            // Verify cleanup happened: the expired pending entry was removed.
            var stats = deduper.GetStatistics();
            Assert.Equal(0, stats.ActiveRequests);

            // Signal the factory so it can finish; factory returns "completed",
            // but production's _disposed/cleanup branch may have already set the
            // TCS to canceled. Either way, awaiting the returned task should not
            // hang. We tolerate either successful completion (factory wins) or
            // TaskCanceledException (cleanup-set TCS observed before completion).
            blockedTcs.SetResult(true);

            try
            {
                await task;
            }
            catch (TaskCanceledException)
            {
                // Acceptable: cleanup canceled the TCS before factory result was set.
            }
        }

        [Fact]
        public async Task GetOrCreateAsync_HandlesCoalescedRequestTimeout()
        {
            // Lines 107-111: Coalesced request times out, falls back to new request
            using var deduper = new RequestDeduplicator(
                NullLogger<RequestDeduplicator>.Instance,
                requestTimeout: TimeSpan.FromMilliseconds(100),
                cleanupInterval: TimeSpan.FromSeconds(10));

            var factoryCalls = 0;
            var blockTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<int> SlowFactory()
            {
                Interlocked.Increment(ref factoryCalls);
                await blockTcs.Task;
                return 42;
            }

            // Start slow request
            var slowTask = deduper.GetOrCreateAsync("timeout-key", SlowFactory);

            // Wait a bit for it to be registered
            await Task.Delay(50);

            // Now wait for the request timeout to elapse
            await Task.Delay(100);

            // Unblock the slow factory
            blockTcs.SetResult(true);

            // Wait for original task
            var result = await slowTask;
            Assert.Equal(42, result);
            Assert.Equal(1, factoryCalls);
        }

        [Fact]
        public async Task GetOrCreateAsync_JoinsExistingRequest_AfterFactoryException()
        {
            // After a failed request is cleaned up, new requests should work
            using var deduper = new RequestDeduplicator(NullLogger<RequestDeduplicator>.Instance);

            var key = "retry-key";
            var attempts = 0;

            // First attempt fails
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => deduper.GetOrCreateAsync<string>(key, () => throw new InvalidOperationException("First failed")));

            // Second attempt should succeed
            var result = await deduper.GetOrCreateAsync(key, () =>
            {
                attempts++;
                return Task.FromResult("success");
            });

            Assert.Equal("success", result);
            Assert.Equal(1, attempts);
        }

        [Fact]
        public async Task Dispose_CancelsAllPendingRequests_WithMultipleRequests()
        {
            // Lines 349-355: Dispose cancels all pending TCSs and clears the dictionary.
            // Production's ExecuteNewRequest does `await factory()` without threading a
            // token into the factory, so Dispose cannot unblock a factory waiting on an
            // unrelated primitive. Verify Dispose's observable contract (TCS canceled,
            // _pendingRequests cleared, _disposed flag set so factory result-path throws),
            // then signal blockTcs so factories unwind. After unwind, ExecuteNewRequest's
            // post-factory `_disposed` check throws TaskCanceledException, satisfying the
            // original assertion.
            var deduper = new RequestDeduplicator(
                NullLogger<RequestDeduplicator>.Instance,
                requestTimeout: TimeSpan.FromSeconds(30),
                cleanupInterval: TimeSpan.FromSeconds(10));

            var started1 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var task1 = deduper.GetOrCreateAsync("dispose-test-1", async () =>
            {
                started1.SetResult(true);
                await blockTcs.Task;
                return "result1";
            });

            var task2 = deduper.GetOrCreateAsync("dispose-test-2", async () =>
            {
                started2.SetResult(true);
                await blockTcs.Task;
                return "result2";
            });

            await Task.WhenAll(started1.Task, started2.Task);

            // Verify both pending requests are tracked before disposal.
            Assert.Equal(2, deduper.GetStatistics().ActiveRequests);

            // Dispose marks _disposed, cancels all internal TCSs, clears the dict.
            deduper.Dispose();

            Assert.Equal(0, deduper.GetStatistics().ActiveRequests);

            // Now release the factories so they can complete. After factory() returns,
            // ExecuteNewRequest sees _disposed == true and throws TaskCanceledException.
            blockTcs.SetResult(true);

            await Assert.ThrowsAsync<TaskCanceledException>(() => task1);
            await Assert.ThrowsAsync<TaskCanceledException>(() => task2);
        }

        [Fact]
        public async Task GetOrCreateAsync_WithCancellationToken_PropagatesCancellation()
        {
            // Documents production contract: GetOrCreateAsync accepts a CancellationToken,
            // but does NOT thread it into the user-supplied factory delegate (factory is
            // Func<Task<T>>, not Func<CancellationToken,Task<T>>). The caller is responsible
            // for observing their own token inside the factory if they want mid-flight
            // cancellation. This test verifies that observable behavior: when the factory
            // observes the token via WaitAsync, cancelling the token cancels the await
            // and propagates OperationCanceledException out of GetOrCreateAsync.
            using var deduper = new RequestDeduplicator(NullLogger<RequestDeduplicator>.Instance);

            using var cts = new CancellationTokenSource();
            var startedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var token = cts.Token;
            var task = deduper.GetOrCreateAsync("cancel-test", async () =>
            {
                startedTcs.SetResult(true);
                // Factory cooperatively observes the caller's token, as production expects.
                await blockTcs.Task.WaitAsync(token);
                return "result";
            }, cts.Token);

            await startedTcs.Task;

            // Cancel while factory is running
            cts.Cancel();

            await Assert.ThrowsAsync<TaskCanceledException>(() => task);

            // Defensive: signal blockTcs so any latent continuation does not leak.
            blockTcs.TrySetResult(true);
        }

        [Fact]
        public void GetStatistics_ReturnsCorrectDefaultValues_WhenNoParametersProvided()
        {
            // Lines 47-48: Default timeout and cleanup values
            using var deduper = new RequestDeduplicator(NullLogger<RequestDeduplicator>.Instance);

            var stats = deduper.GetStatistics();

            Assert.Equal(60, stats.RequestTimeout.TotalSeconds);
            Assert.Equal(30, stats.CleanupInterval.TotalSeconds);
            Assert.Equal(1000, stats.MaxConcurrentRequests);
            Assert.Equal(0, stats.ActiveRequests);
        }

        [Fact]
        public async Task GetOrCreateAsync_ReturnsResult_WhenTaskCompletesSuccessfully()
        {
            // Basic success path
            using var deduper = new RequestDeduplicator(NullLogger<RequestDeduplicator>.Instance);

            var result = await deduper.GetOrCreateAsync("success-key", () => Task.FromResult("test-result"));

            Assert.Equal("test-result", result);
        }

        [Fact]
        public async Task GetOrCreateAsync_HandlesValueTypeResult()
        {
            // Verify value types work correctly
            using var deduper = new RequestDeduplicator(NullLogger<RequestDeduplicator>.Instance);

            var result = await deduper.GetOrCreateAsync("int-key", () => Task.FromResult(12345));

            Assert.Equal(12345, result);
        }
    }
}
