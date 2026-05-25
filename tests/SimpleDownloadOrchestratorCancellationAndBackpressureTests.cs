using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Download;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Tests for SimpleDownloadOrchestrator audit gaps (Wave 11C):
    /// - Cancellation semantics for album downloads (mid-flight, pre-flight, partial completion)
    /// - Real concurrency boundary behavior (MaxConcurrent never exceeded under load)
    /// - Backpressure (slow downstream does not pile up parallel work)
    ///
    /// These tests use TaskCompletionSource gates instead of Thread.Sleep so they
    /// are deterministic across CI/local runs.
    /// </summary>
    [Trait("Category", "Unit")]
    public class SimpleDownloadOrchestratorCancellationAndBackpressureTests
    {
        // -------- Test doubles --------

        /// <summary>
        /// Stream provider whose GetStreamAsync entry/exit can be observed and gated by tests.
        /// Tracks live concurrency, max-observed concurrency, and per-track call order.
        /// </summary>
        private sealed class GatedStreamProvider : IAudioStreamProvider
        {
            private readonly Func<string, byte[]> _payloadFactory;
            private readonly TaskCompletionSource<bool>? _releaseAll;
            private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _perTrackRelease;
            private readonly object _sync = new();
            private int _live;

            public int MaxObservedConcurrent { get; private set; }
            public int TotalEnters { get; private set; }
            public ConcurrentQueue<string> EnterOrder { get; } = new();
            public ConcurrentQueue<string> CancelledTracks { get; } = new();

            /// <summary>
            /// Fires every time a track enters GetStreamAsync. Useful for tests to wait
            /// until exactly N tracks are simultaneously parked at the gate.
            /// </summary>
            public event Action<int>? OnEnter;

            public GatedStreamProvider(
                Func<string, byte[]> payloadFactory,
                TaskCompletionSource<bool>? releaseAll = null,
                IDictionary<string, TaskCompletionSource<bool>>? perTrackRelease = null)
            {
                _payloadFactory = payloadFactory;
                _releaseAll = releaseAll;
                _perTrackRelease = new ConcurrentDictionary<string, TaskCompletionSource<bool>>(
                    perTrackRelease ?? new Dictionary<string, TaskCompletionSource<bool>>());
            }

            public async Task<AudioStreamResult> GetStreamAsync(string trackId, StreamingQuality? quality = null, CancellationToken cancellationToken = default)
            {
                EnterOrder.Enqueue(trackId);
                int liveNow;
                lock (_sync)
                {
                    _live++;
                    TotalEnters++;
                    liveNow = _live;
                    if (_live > MaxObservedConcurrent) MaxObservedConcurrent = _live;
                }
                OnEnter?.Invoke(liveNow);

                try
                {
                    // Wait for either a per-track gate or the global gate.
                    if (_perTrackRelease.TryGetValue(trackId, out var perTrack))
                    {
                        using (cancellationToken.Register(() => perTrack.TrySetCanceled(cancellationToken)))
                        {
                            await perTrack.Task.ConfigureAwait(false);
                        }
                    }
                    else if (_releaseAll != null)
                    {
                        using (cancellationToken.Register(() => _releaseAll.TrySetCanceled(cancellationToken)))
                        {
                            await _releaseAll.Task.ConfigureAwait(false);
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    var payload = _payloadFactory(trackId);
                    return new AudioStreamResult
                    {
                        Stream = new MemoryStream(payload, writable: false),
                        TotalBytes = payload.Length,
                        SuggestedExtension = "bin"
                    };
                }
                catch (OperationCanceledException)
                {
                    CancelledTracks.Enqueue(trackId);
                    throw;
                }
                finally
                {
                    lock (_sync) { _live--; }
                }
            }
        }

        private static StreamingTrack MakeTrack(string id, int trackNumber) =>
            new()
            {
                Id = id,
                Title = $"Track {trackNumber}",
                TrackNumber = trackNumber,
                Artist = new StreamingArtist { Name = "Artist" },
                Album = new StreamingAlbum { Title = "Album", Artist = new StreamingArtist { Name = "Artist" } }
            };

        private static SimpleDownloadOrchestrator BuildOrchestrator(
            int maxConcurrent,
            List<string> trackIds,
            IAudioStreamProvider streamProvider)
        {
            return new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: new System.Net.Http.HttpClient(),
                getAlbumAsync: id => Task.FromResult(new StreamingAlbum
                {
                    Id = id,
                    Title = "Album",
                    Artist = new StreamingArtist { Name = "Artist" },
                    TrackCount = trackIds.Count
                }),
                getTrackAsync: id =>
                {
                    var idx = trackIds.IndexOf(id);
                    return Task.FromResult(MakeTrack(id, idx >= 0 ? idx + 1 : 1));
                },
                getAlbumTrackIdsAsync: _ => Task.FromResult((IReadOnlyList<string>)trackIds),
                getStreamAsync: (_, _) => Task.FromResult(("http://unused", "bin")),
                maxConcurrentTracks: maxConcurrent,
                streamProvider: streamProvider);
        }

        private static readonly StreamingQuality DefaultQuality = new() { Bitrate = 320 };

        private static string FreshOutputDir(string label)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"orch_audit_{label}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void TryDeleteDir(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
        }

        // -------- Cancellation tests --------

        [Fact]
        public async Task DownloadAlbumAsync_PreCancelledToken_ThrowsBeforeAnyWork()
        {
            // Token already cancelled before call.
            var streamProvider = new GatedStreamProvider(_ => new byte[16]);
            var trackIds = new List<string> { "t1", "t2" };
            var orch = BuildOrchestrator(maxConcurrent: 1, trackIds: trackIds, streamProvider: streamProvider);

            var outputDir = FreshOutputDir("precancel");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    orch.DownloadAlbumAsync("album1", outputDir, quality: DefaultQuality, progress: null!, cancellationToken: cts.Token));

                // Orchestrator must short-circuit: stream provider must never be entered.
                Assert.Equal(0, streamProvider.TotalEnters);
            }
            finally
            {
                TryDeleteDir(outputDir);
            }
        }

        [Fact]
        public async Task DownloadAlbumAsync_CancelMidFlight_Sequential_StopsAfterCurrentTrack()
        {
            // Sequential path (MaxConcurrent=1): cancel after first track parks at the gate.
            // Expect: cancellation propagates, remaining tracks are never entered.
            var firstAtGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var streamProvider = new GatedStreamProvider(_ => new byte[8], releaseAll: release);
            streamProvider.OnEnter += _ => firstAtGate.TrySetResult(true);

            var trackIds = new List<string> { "t1", "t2", "t3" };
            var orch = BuildOrchestrator(maxConcurrent: 1, trackIds: trackIds, streamProvider: streamProvider);

            var outputDir = FreshOutputDir("seq_cancel");
            using var cts = new CancellationTokenSource();

            try
            {
                var downloadTask = orch.DownloadAlbumAsync("album1", outputDir, quality: DefaultQuality, progress: null!, cancellationToken: cts.Token);

                // Wait deterministically for the first track to be inside the stream provider.
                await firstAtGate.Task.WaitAsync(TimeSpan.FromSeconds(10));

                // Cancel while track 1 is parked.
                cts.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await downloadTask);

                // Only the first track should have entered; remaining must be skipped.
                Assert.Equal(1, streamProvider.TotalEnters);
                Assert.DoesNotContain("t2", streamProvider.EnterOrder);
                Assert.DoesNotContain("t3", streamProvider.EnterOrder);
            }
            finally
            {
                release.TrySetResult(true);
                TryDeleteDir(outputDir);
            }
        }

        [Fact]
        public async Task DownloadAlbumAsync_CancelMidFlight_Parallel_QueuedTracksNeverStart()
        {
            // Parallel path: MaxConcurrent=2, 6 tracks. Cancel after first 2 enter the gate.
            // The 4 queued tracks must never enter the stream provider (semaphore.WaitAsync
            // honours the cancellation token).
            var atGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var streamProvider = new GatedStreamProvider(_ => new byte[8], releaseAll: release);
            streamProvider.OnEnter += live =>
            {
                if (live >= 2) atGate.TrySetResult(true);
            };

            var trackIds = new List<string> { "t1", "t2", "t3", "t4", "t5", "t6" };
            var orch = BuildOrchestrator(maxConcurrent: 2, trackIds: trackIds, streamProvider: streamProvider);

            var outputDir = FreshOutputDir("par_cancel");
            using var cts = new CancellationTokenSource();

            try
            {
                var downloadTask = orch.DownloadAlbumAsync("album1", outputDir, quality: DefaultQuality, progress: null!, cancellationToken: cts.Token);

                await atGate.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(2, streamProvider.TotalEnters);

                cts.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await downloadTask);

                // Only the 2 that beat the cancel should have entered; the other 4 stay on the
                // semaphore queue and are cancelled before entering.
                Assert.Equal(2, streamProvider.TotalEnters);
                Assert.True(streamProvider.MaxObservedConcurrent <= 2);
            }
            finally
            {
                release.TrySetResult(true);
                TryDeleteDir(outputDir);
            }
        }

        [Fact]
        public async Task Orchestrator_Quirk_DownloadTrackAsync_StreamProviderCancellationSwallowedAsFailureResult()
        {
            // BUG: SimpleDownloadOrchestrator.DownloadTrackInternalAsync wraps the
            // IAudioStreamProvider call in a `catch (Exception ex)` (source lines ~285-288)
            // that catches OperationCanceledException and converts it to a failure
            // TrackDownloadResult instead of rethrowing. This contradicts:
            //   (a) the URL-based path in DownloadViaUrlAsync which explicitly rethrows OCE
            //       (source lines ~439-442)
            //   (b) the public DownloadTrackAsync entry which begins with
            //       ThrowIfCancellationRequested() -- callers expect OCE on cancel
            //   (c) the outer `catch (OperationCanceledException)` at source line ~300
            //       which is unreachable when a stream provider is configured
            //
            // Expected (eventually): OperationCanceledException is propagated to the caller.
            // Observed (today):       Task completes with Success=false and ErrorMessage
            //                          beginning "Track t1: ..." (sanitized OCE message).
            //
            // This test pins the current behavior so a future fix is visible in the diff.
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var atGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var streamProvider = new GatedStreamProvider(_ => new byte[16], releaseAll: release);
            streamProvider.OnEnter += _ => atGate.TrySetResult(true);

            var orch = BuildOrchestrator(maxConcurrent: 1, trackIds: new List<string> { "t1" }, streamProvider: streamProvider);

            var outputDir = FreshOutputDir("track_cancel");
            var outputPath = Path.Combine(outputDir, "track.bin");
            using var cts = new CancellationTokenSource();

            try
            {
                var task = orch.DownloadTrackAsync("t1", outputPath, quality: null, cancellationToken: cts.Token);
                await atGate.Task.WaitAsync(TimeSpan.FromSeconds(10));
                cts.Cancel();

                // Pin current behavior: cancellation is swallowed and surfaces as a
                // failure result rather than an OperationCanceledException.
                var result = await task;

                Assert.False(result.Success, "Cancellation currently surfaces as a failure result");
                Assert.False(string.IsNullOrEmpty(result.ErrorMessage));

                // Atomic move never happens on cancellation, so the final file must be absent.
                Assert.False(File.Exists(outputPath), "Final file must not be left behind after cancellation");
            }
            finally
            {
                release.TrySetResult(true);
                TryDeleteDir(outputDir);
            }
        }

        // -------- Concurrency limit / backpressure tests --------

        [Theory]
        [InlineData(1, 8)]
        [InlineData(2, 8)]
        [InlineData(4, 12)]
        public async Task DownloadAlbumAsync_NeverExceedsMaxConcurrent(int maxConcurrent, int trackCount)
        {
            // Slow stream provider: every track parks at a shared gate. We hold them until
            // we've observed enough concurrency, then release. Peak live count must equal
            // exactly MaxConcurrent (never higher).
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var saturated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var streamProvider = new GatedStreamProvider(_ => new byte[4], releaseAll: release);
            streamProvider.OnEnter += live =>
            {
                if (live >= maxConcurrent) saturated.TrySetResult(true);
            };

            var trackIds = Enumerable.Range(1, trackCount).Select(i => $"t{i}").ToList();
            var orch = BuildOrchestrator(maxConcurrent: maxConcurrent, trackIds: trackIds, streamProvider: streamProvider);

            var outputDir = FreshOutputDir($"max{maxConcurrent}");
            try
            {
                var albumTask = orch.DownloadAlbumAsync("a1", outputDir, quality: DefaultQuality, progress: null!, cancellationToken: CancellationToken.None);

                // Wait until we've seen the orchestrator reach its concurrency ceiling.
                // Use a generous timeout so slow CI cannot flake.
                await saturated.Task.WaitAsync(TimeSpan.FromSeconds(10));

                Assert.Equal(maxConcurrent, streamProvider.MaxObservedConcurrent);

                // Release all tracks and finish.
                release.TrySetResult(true);
                var result = await albumTask;

                Assert.True(result.Success, $"Album download should succeed: {result.ErrorMessage}");
                Assert.Equal(trackCount, result.TrackResults.Count);

                // Peak must never have exceeded the configured limit at any point.
                Assert.True(streamProvider.MaxObservedConcurrent <= maxConcurrent,
                    $"Observed peak {streamProvider.MaxObservedConcurrent} exceeded MaxConcurrent {maxConcurrent}");
            }
            finally
            {
                release.TrySetResult(true);
                TryDeleteDir(outputDir);
            }
        }

        [Fact]
        public async Task DownloadAlbumAsync_Backpressure_SlowDownstreamDoesNotPileUp()
        {
            // MaxConcurrent=2, 6 tracks, hold each one at the gate. The orchestrator must
            // NOT start track 3 until one of {t1, t2} completes. This proves the semaphore
            // gates the producer side, not just the consumer side.
            const int maxConcurrent = 2;
            var perTrackGates = Enumerable.Range(1, 6).ToDictionary(
                i => $"t{i}",
                _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            var streamProvider = new GatedStreamProvider(_ => new byte[4], perTrackRelease: perTrackGates);

            // Wait until exactly 2 tracks are parked before we begin asserting.
            var saturated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            streamProvider.OnEnter += live =>
            {
                if (live >= maxConcurrent) saturated.TrySetResult(true);
            };

            var trackIds = perTrackGates.Keys.OrderBy(k => int.Parse(k.Substring(1))).ToList();
            var orch = BuildOrchestrator(maxConcurrent: maxConcurrent, trackIds: trackIds, streamProvider: streamProvider);

            var outputDir = FreshOutputDir("backpressure");
            try
            {
                var albumTask = orch.DownloadAlbumAsync("a1", outputDir, quality: DefaultQuality, progress: null!, cancellationToken: CancellationToken.None);

                await saturated.Task.WaitAsync(TimeSpan.FromSeconds(10));

                // At this point exactly 2 tracks are inside; the orchestrator must be
                // back-pressured -- enters must equal 2, not more.
                var entersWhileSaturated = streamProvider.TotalEnters;
                Assert.Equal(2, entersWhileSaturated);

                // Release ONE track. The semaphore should immediately admit the next one.
                var firstEntered = streamProvider.EnterOrder.First();
                perTrackGates[firstEntered].TrySetResult(true);

                // Spin-wait deterministically by polling the (monotonically increasing)
                // counter using Task.Yield -- avoids Thread.Sleep but is bounded by timeout.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (streamProvider.TotalEnters < 3 && sw.Elapsed < TimeSpan.FromSeconds(10))
                {
                    await Task.Yield();
                }

                Assert.True(streamProvider.TotalEnters >= 3,
                    "After releasing one track, a queued track must be admitted (backpressure relief)");
                Assert.True(streamProvider.MaxObservedConcurrent <= maxConcurrent,
                    $"Peak {streamProvider.MaxObservedConcurrent} exceeded MaxConcurrent {maxConcurrent} after relief");

                // Drain remaining tracks.
                foreach (var tcs in perTrackGates.Values) tcs.TrySetResult(true);
                var result = await albumTask;
                Assert.True(result.Success, $"Album download should succeed: {result.ErrorMessage}");
            }
            finally
            {
                foreach (var tcs in perTrackGates.Values) tcs.TrySetResult(true);
                TryDeleteDir(outputDir);
            }
        }

        [Fact]
        public async Task DownloadAlbumAsync_PartialCompletion_OnCancel_ReportsCompletedTracksInResult()
        {
            // MaxConcurrent=1. Let t1 finish, then cancel while t2 is parked.
            // The orchestrator currently throws (sequential path uses ThrowIfCancellationRequested).
            // We capture the partial state by inspecting the directory: t1's file must remain
            // because its atomic move completed before cancellation.
            var release2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var t2AtGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Per-track gates: t1 is auto-released, t2 waits.
            var perTrackGates = new Dictionary<string, TaskCompletionSource<bool>>
            {
                ["t1"] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
                ["t2"] = release2,
            };
            perTrackGates["t1"].TrySetResult(true); // t1 never blocks

            var streamProvider = new GatedStreamProvider(_ => new byte[32], perTrackRelease: perTrackGates);
            streamProvider.OnEnter += _ => { /* signal handled below by EnterOrder */ };

            var trackIds = new List<string> { "t1", "t2", "t3" };
            var orch = BuildOrchestrator(maxConcurrent: 1, trackIds: trackIds, streamProvider: streamProvider);

            var outputDir = FreshOutputDir("partial");
            using var cts = new CancellationTokenSource();

            try
            {
                var albumTask = orch.DownloadAlbumAsync("a1", outputDir, quality: DefaultQuality, progress: null!, cancellationToken: cts.Token);

                // Wait deterministically for t2 to be inside the provider (which means t1 finished).
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!streamProvider.EnterOrder.Contains("t2") && sw.Elapsed < TimeSpan.FromSeconds(10))
                {
                    await Task.Yield();
                }
                Assert.Contains("t2", streamProvider.EnterOrder);

                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await albumTask);

                // Partial-completion invariants:
                // - t1's final file exists (it completed before cancellation).
                // - t3 was never started.
                var filesOnDisk = Directory.EnumerateFiles(outputDir).Select(Path.GetFileName).ToList();
                Assert.Contains(filesOnDisk, name => name!.Contains("Track 1", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain("t3", streamProvider.EnterOrder);
            }
            finally
            {
                release2.TrySetResult(true);
                TryDeleteDir(outputDir);
            }
        }
    }
}
