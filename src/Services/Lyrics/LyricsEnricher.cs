using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
                // Off the caller thread — TagLib open/save is blocking disk I/O.
                await Task.Run(() => TryEmbedLyrics(audioFilePath, lyrics), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
                // The .lrc sidecar keeps the timestamped synced form for capable players; the unsynced
                // Tag.Lyrics (USLT / UNSYNCEDLYRICS) should hold clean text so players don't render the
                // raw "[00:01.23]" timing codes to the user.
                file.Tag.Lyrics = StripLrcTimestamps(lyrics);
                file.Save();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Embedding lyrics into tag failed for {File} (non-fatal)", Path.GetFileName(audioFilePath));
            }
        }

        private static readonly Regex LrcTimestamp = new(@"\[\d{1,2}:\d{2}(?:[.:]\d{1,3})?\]", RegexOptions.Compiled);

        /// <summary>Removes synced-LRC timing tags (<c>[mm:ss.xx]</c>) so the embedded unsynced lyrics read cleanly.</summary>
        private static string StripLrcTimestamps(string lyrics)
        {
            if (string.IsNullOrEmpty(lyrics))
            {
                return lyrics;
            }

            // Drop the timing tags, then trim the leading space each leaves behind, per line.
            var stripped = LrcTimestamp.Replace(lyrics, string.Empty);
            return string.Join('\n', stripped.Split('\n').Select(l => l.TrimStart()));
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
