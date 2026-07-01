using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Lyrics;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Lyrics
{
    /// <summary>
    /// Contract suite for the consolidated lyrics enricher (source of truth for tidal + qobuz).
    /// Canonical behavior: a native source is tried first; LRCLIB is a fallback gated by
    /// <c>allowLrclibFallback</c>; the found LRC is written next to the audio file; everything is
    /// best-effort (a lyrics failure never throws and never writes a partial file).
    /// </summary>
    public class LyricsEnricherTests
    {
        private sealed class StubNativeSource : INativeLyricsSource
        {
            private readonly string? _result;
            public int Calls { get; private set; }
            public StubNativeSource(string? result) => _result = result;
            public Task<string?> TryGetSyncedLyricsAsync(string artistName, string trackName, string albumName, int durationSeconds, CancellationToken ct = default)
            {
                Calls++;
                return Task.FromResult(_result);
            }
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpResponseMessage> _factory;
            public int Calls { get; private set; }
            public StubHandler(Func<HttpResponseMessage> factory) => _factory = factory;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                return Task.FromResult(_factory());
            }
        }

        private static HttpResponseMessage LrclibHit(string synced)
            => new(HttpStatusCode.OK) { Content = new StringContent($"{{\"syncedLyrics\":\"{synced}\"}}") };

        private static HttpResponseMessage LrclibMiss() => new(HttpStatusCode.NotFound);

        private static LyricsEnricher Enricher(INativeLyricsSource? native, StubHandler handler)
            => new(native, new LrclibClient(new HttpClient(handler)));

        private sealed class TempDir : IDisposable
        {
            public string Dir { get; }
            public TempDir() { Dir = Path.Combine(Path.GetTempPath(), "lyr-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Dir); }
            public string Audio() { var p = Path.Combine(Dir, "song.flac"); File.WriteAllText(p, "x"); return p; }
            // A structurally-valid minimal FLAC so TagLib can open + tag it (embed test).
            public string RealFlac()
            {
                var p = Path.Combine(Dir, "real.flac");
                File.WriteAllBytes(p, new byte[]
                {
                    0x66, 0x4C, 0x61, 0x43,
                    0x80, 0x00, 0x00, 0x22,
                    0x00, 0x10, 0x00, 0x10,
                    0x00, 0x00, 0x01, 0x00, 0x00, 0x01,
                    0x0A, 0xC4, 0x40, 0xF0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
                    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                    0xFF, 0xF8, 0x09, 0x18, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
                });
                return p;
            }
            public static string Lrc(string audio) => Path.ChangeExtension(audio, ".lrc");
            public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
        }

        [Fact]
        public async Task Prefers_native_source_and_skips_lrclib_on_native_hit()
        {
            using var tmp = new TempDir();
            var audio = tmp.Audio();
            var native = new StubNativeSource("[00:00.00]native");
            var handler = new StubHandler(() => LrclibHit("[00:00.00]lrclib"));
            using var sut = Enricher(native, handler);

            await sut.TryEnrichAsync(audio, "Artist", "Track", "Album", 100, allowLrclibFallback: true);

            Assert.Equal(1, native.Calls);
            Assert.Equal(0, handler.Calls);
            Assert.Contains("native", await File.ReadAllTextAsync(TempDir.Lrc(audio)));
        }

        [Fact]
        public async Task Falls_back_to_lrclib_when_native_empty_and_allowed()
        {
            using var tmp = new TempDir();
            var audio = tmp.Audio();
            var native = new StubNativeSource(null);
            var handler = new StubHandler(() => LrclibHit("[00:00.00]lrclib"));
            using var sut = Enricher(native, handler);

            await sut.TryEnrichAsync(audio, "Artist", "Track", "Album", 100, allowLrclibFallback: true);

            Assert.Equal(1, handler.Calls);
            Assert.Contains("lrclib", await File.ReadAllTextAsync(TempDir.Lrc(audio)));
        }

        [Fact]
        public async Task Does_not_consult_lrclib_when_fallback_disabled()
        {
            using var tmp = new TempDir();
            var audio = tmp.Audio();
            var native = new StubNativeSource(null);
            var handler = new StubHandler(() => LrclibHit("[00:00.00]lrclib"));
            using var sut = Enricher(native, handler);

            await sut.TryEnrichAsync(audio, "Artist", "Track", "Album", 100, allowLrclibFallback: false);

            Assert.Equal(0, handler.Calls);
            Assert.False(File.Exists(TempDir.Lrc(audio)));
        }

        [Fact]
        public async Task Uses_lrclib_when_no_native_source_supplied()
        {
            using var tmp = new TempDir();
            var audio = tmp.Audio();
            var handler = new StubHandler(() => LrclibHit("[00:00.00]only-lrclib"));
            using var sut = Enricher(null, handler);

            await sut.TryEnrichAsync(audio, "Artist", "Track", "Album", 100, allowLrclibFallback: true);

            Assert.Contains("only-lrclib", await File.ReadAllTextAsync(TempDir.Lrc(audio)));
        }

        [Fact]
        public async Task Writes_nothing_when_no_lyrics_found()
        {
            using var tmp = new TempDir();
            var audio = tmp.Audio();
            var handler = new StubHandler(LrclibMiss);
            using var sut = Enricher(null, handler);

            await sut.TryEnrichAsync(audio, "Artist", "Track", "Album", 100, allowLrclibFallback: true);

            Assert.False(File.Exists(TempDir.Lrc(audio)));
        }

        [Theory]
        [InlineData("", "Track")]
        [InlineData("Artist", "")]
        [InlineData("   ", "Track")]
        public async Task Skips_when_artist_or_track_missing(string artist, string track)
        {
            using var tmp = new TempDir();
            var audio = tmp.Audio();
            var handler = new StubHandler(() => LrclibHit("x"));
            using var sut = Enricher(null, handler);

            await sut.TryEnrichAsync(audio, artist, track, "Album", 100, allowLrclibFallback: true);

            Assert.Equal(0, handler.Calls);
            Assert.False(File.Exists(TempDir.Lrc(audio)));
        }

        [Fact]
        public async Task Is_best_effort_when_source_throws()
        {
            using var tmp = new TempDir();
            var audio = tmp.Audio();
            var native = new StubNativeSource(null);
            var handler = new StubHandler(() => throw new HttpRequestException("boom"));
            using var sut = Enricher(native, handler);

            var ex = await Record.ExceptionAsync(() =>
                sut.TryEnrichAsync(audio, "Artist", "Track", "Album", 100, allowLrclibFallback: true));

            Assert.Null(ex);
            Assert.False(File.Exists(TempDir.Lrc(audio)));
        }

        [Fact]
        public async Task Embeds_lyrics_into_audio_tag_so_they_survive_import()
        {
            using var tmp = new TempDir();
            var audio = tmp.RealFlac();
            var native = new StubNativeSource("[00:00.00]hello world lyrics");
            var handler = new StubHandler(() => LrclibHit("x"));
            using var sut = Enricher(native, handler);

            await sut.TryEnrichAsync(audio, "Artist", "Track", "Album", 100, allowLrclibFallback: true);

            // Sidecar is still written for synced-lyrics-capable players ...
            Assert.True(File.Exists(TempDir.Lrc(audio)));
            // ... AND embedded into the tag so it survives Lidarr import (importExtraFiles=false drops sidecars).
            using var file = TagLib.File.Create(audio);
            Assert.Contains("hello world lyrics", file.Tag.Lyrics ?? string.Empty);
        }
    }
}
