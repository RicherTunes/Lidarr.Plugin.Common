using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Lyrics
{
    /// <summary>
    /// Canonical synced-lyrics enricher shared across plugins (replaces the duplicated per-plugin
    /// copies). Orchestration only: native-source-first, LRCLIB fallback (gated by the caller),
    /// write the <c>.lrc</c> next to the audio. Service-specific fetching belongs in an
    /// <see cref="INativeLyricsSource"/>; the per-feature gating (e.g. SaveSyncedLyrics / UseLRCLIB)
    /// stays in the plugin that owns the settings UI and is expressed via the call arguments.
    /// </summary>
    public sealed class LyricsEnricher : ILyricsEnricher
    {
        private readonly INativeLyricsSource? _nativeSource;
        private readonly LrclibClient _lrclib;
        private readonly bool _ownsLrclib;
        private readonly ILogger? _logger;

        /// <summary>
        /// Creates an enricher with an internally-owned <see cref="LrclibClient"/> (disposed with
        /// this instance). Convenient for DI registration; long-lived consumers should register this
        /// as a singleton so the LRCLIB HttpClient is reused.
        /// </summary>
        public LyricsEnricher(INativeLyricsSource? nativeSource = null, ILogger? logger = null)
            : this(nativeSource, new LrclibClient(), ownsLrclib: true, logger)
        {
        }

        /// <summary>
        /// Creates an enricher over a caller-owned <see cref="LrclibClient"/> (not disposed here).
        /// Use when the LRCLIB client should share the consumer's HttpClient/handler chain, or for tests.
        /// </summary>
        public LyricsEnricher(INativeLyricsSource? nativeSource, LrclibClient lrclibClient, ILogger? logger = null)
            : this(nativeSource, lrclibClient ?? throw new ArgumentNullException(nameof(lrclibClient)), ownsLrclib: false, logger)
        {
        }

        private LyricsEnricher(INativeLyricsSource? nativeSource, LrclibClient lrclibClient, bool ownsLrclib, ILogger? logger)
        {
            _nativeSource = nativeSource;
            _lrclib = lrclibClient;
            _ownsLrclib = ownsLrclib;
            _logger = logger;
        }

        /// <inheritdoc/>
        public async Task TryEnrichAsync(string audioFilePath, string artistName, string trackName, string albumName, int durationSeconds, bool allowLrclibFallback, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(artistName) || string.IsNullOrWhiteSpace(trackName))
            {
                return;
            }

            var album = albumName ?? string.Empty;
            var duration = Math.Max(0, durationSeconds);

            try
            {
                string? lyrics = null;

                if (_nativeSource is not null)
                {
                    lyrics = await _nativeSource.TryGetSyncedLyricsAsync(artistName, trackName, album, duration, cancellationToken).ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(lyrics) && allowLrclibFallback)
                {
                    lyrics = await _lrclib.TryFetchSyncedLyricsAsync(artistName, trackName, album, duration, cancellationToken).ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(lyrics))
                {
                    return;
                }

                var lrcPath = Path.ChangeExtension(audioFilePath, ".lrc");
                await File.WriteAllTextAsync(lrcPath, lyrics, cancellationToken).ConfigureAwait(false);
                _logger?.LogDebug("Saved synced lyrics: {File}", Path.GetFileName(lrcPath));

                // Also embed the lyrics into the audio tag. The .lrc sidecar above serves synced-lyrics
                // players, but Lidarr's default importExtraFiles=false drops sidecars at import — so
                // without embedding, lyrics never reach the library. Best-effort: never fail the download.
                TryEmbedLyrics(audioFilePath, lyrics);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Lyrics enrichment failed for {Artist} - {Track} (non-fatal)", artistName, trackName);
            }
        }

        /// <summary>
        /// Best-effort embed of the lyrics text into the audio file's tag (FLAC UNSYNCEDLYRICS /
        /// ID3 USLT via TagLib's unified <c>Tag.Lyrics</c>). Any failure (non-audio file, unsupported
        /// container) is swallowed — lyrics enrichment must never fail a download.
        /// </summary>
        private void TryEmbedLyrics(string audioFilePath, string lyrics)
        {
            try
            {
                using var file = TagLib.File.Create(audioFilePath);
                file.Tag.Lyrics = lyrics;
                file.Save();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Embedding lyrics into tag failed for {File} (non-fatal)", Path.GetFileName(audioFilePath));
            }
        }

        public void Dispose()
        {
            if (_ownsLrclib)
            {
                _lrclib.Dispose();
            }
        }
    }
}
