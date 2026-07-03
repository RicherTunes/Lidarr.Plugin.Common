using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Download;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Regression tests for the <see cref="IAudioStreamProvider"/> download branch of
    /// <see cref="SimpleDownloadOrchestrator"/>. The provider hands back a fully-assembled stream with no
    /// byte-range resume, so a transient blip mid-copy (a truncated provider stream, a per-request timeout)
    /// must re-invoke the whole provider and recover — rather than failing the track, which
    /// <see cref="AlbumCompletionPolicy"/> would escalate to a whole-album failure and a host re-grab loop.
    /// A NON-transient error (auth/DRM) must NOT be retried (re-acquiring a Widevine license is expensive),
    /// and a persistently-failing provider must ultimately fail cleanly with no lingering partial.
    /// </summary>
    public sealed class SimpleDownloadOrchestratorStreamProviderRetryTests
    {
        private sealed class NoOpMetadataApplier : IAudioMetadataApplier
        {
            public Task ApplyAsync(string filePath, StreamingTrack metadata, CancellationToken cancellationToken = default)
                => Task.CompletedTask;
        }

        // Zero backoff + configurable attempt cap so tests are fast and deterministic.
        private sealed class FastRetryProviderOrchestrator : SimpleDownloadOrchestrator
        {
            public FastRetryProviderOrchestrator(IAudioStreamProvider provider, int maxAttempts)
                : base(
                    serviceName: "Test",
                    httpClient: new HttpClient(),
                    getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1 }),
                    getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", Artist = new StreamingArtist { Name = "X" }, TrackNumber = 1 }),
                    getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                    getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/unused", "bin")),
                    maxConcurrentTracks: 1,
                    streamProvider: provider,
                    metadataApplier: new NoOpMetadataApplier())
            {
                MaxStreamProviderAttempts = maxAttempts;
            }

            internal override int MaxStreamProviderAttempts { get; }
            internal override TimeSpan GetRetryDelay(int attempt) => TimeSpan.Zero;
        }

        // Emits `emit` deterministic bytes then throws `ex` on the next read (simulating a mid-body truncation
        // of the assembled provider stream). emit == 0 throws immediately.
        private sealed class ThrowAfterStream : Stream
        {
            private readonly int _emit;
            private readonly Exception _ex;
            private int _pos;

            public ThrowAfterStream(int emit, Exception ex) { _emit = emit; _ex = ex; }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_pos >= _emit) throw _ex;
                int n = Math.Min(buffer.Length, _emit - _pos);
                for (int i = 0; i < n; i++) buffer.Span[i] = (byte)((_pos + i) % 251);
                _pos += n;
                return ValueTask.FromResult(n);
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _pos; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // Serves a scripted sequence of provider responses; each factory either returns an AudioStreamResult or
        // throws (to model the provider failing before it even returns a stream, e.g. a license/auth error).
        private sealed class SequencedStreamProvider : IAudioStreamProvider
        {
            private readonly Queue<Func<AudioStreamResult>> _responses;
            public int Calls;

            public SequencedStreamProvider(params Func<AudioStreamResult>[] responses)
                => _responses = new Queue<Func<AudioStreamResult>>(responses);

            public Task<AudioStreamResult> GetStreamAsync(string trackId, StreamingQuality? quality = null, CancellationToken cancellationToken = default)
            {
                Calls++;
                if (_responses.Count == 0) throw new InvalidOperationException("SequencedStreamProvider exhausted");
                return Task.FromResult(_responses.Dequeue()());
            }
        }

        private static AudioStreamResult Good(byte[] payload) =>
            new() { Stream = new MemoryStream(payload), TotalBytes = payload.Length, SuggestedExtension = "bin" };

        private static AudioStreamResult Truncating(int emit, Exception ex) =>
            new() { Stream = new ThrowAfterStream(emit, ex), SuggestedExtension = "bin" };

        private static void Cleanup(string temp)
        {
            var bin = Path.ChangeExtension(temp, "bin");
            foreach (var p in new[] { temp, bin, temp + ".partial", bin + ".partial" })
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            }
        }

        [Fact]
        public async Task StreamProvider_retries_transient_midcopy_truncation_and_succeeds()
        {
            var payload = new byte[4096];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);

            var provider = new SequencedStreamProvider(
                () => Truncating(500, new IOException("simulated mid-copy truncation")),
                () => Good(payload));
            var orch = new FastRetryProviderOrchestrator(provider, maxAttempts: 2);

            var temp = Path.Combine(Path.GetTempPath(), $"orch_prov_{Guid.NewGuid():N}.bin");
            try
            {
                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 }, CancellationToken.None);

                Assert.True(result.Success, $"Provider download should recover after a transient blip; error: {result.ErrorMessage}");
                Assert.True(File.Exists(result.FilePath));
                Assert.Equal(payload.Length, new FileInfo(result.FilePath).Length);
                Assert.Equal(2, provider.Calls); // re-acquired exactly once
            }
            finally { Cleanup(temp); }
        }

        [Fact]
        public async Task StreamProvider_does_not_retry_nontransient_failure()
        {
            var provider = new SequencedStreamProvider(
                () => throw new InvalidOperationException("license/auth failure — not transient"));
            var orch = new FastRetryProviderOrchestrator(provider, maxAttempts: 3);

            var temp = Path.Combine(Path.GetTempPath(), $"orch_prov_nt_{Guid.NewGuid():N}.bin");
            try
            {
                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 }, CancellationToken.None);

                Assert.False(result.Success, "A non-transient provider error must fail the track, not succeed");
                Assert.Equal(1, provider.Calls); // NOT retried — DRM re-license is expensive
            }
            finally { Cleanup(temp); }
        }

        [Fact]
        public async Task StreamProvider_persistent_transient_failure_exhausts_retries_and_cleans_partial()
        {
            var provider = new SequencedStreamProvider(
                () => Truncating(500, new IOException("blip 1")),
                () => Truncating(500, new IOException("blip 2")));
            var orch = new FastRetryProviderOrchestrator(provider, maxAttempts: 2);

            var temp = Path.Combine(Path.GetTempPath(), $"orch_prov_fail_{Guid.NewGuid():N}.bin");
            try
            {
                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 }, CancellationToken.None);

                Assert.False(result.Success, "A persistently truncating provider must ultimately fail");
                Assert.Equal(2, provider.Calls);

                // The half-written partial must be cleaned up on final failure — no lingering temp.
                var bin = Path.ChangeExtension(temp, "bin");
                Assert.False(File.Exists(bin + ".partial"), "partial should be deleted after the final failure");
            }
            finally { Cleanup(temp); }
        }
    }
}
