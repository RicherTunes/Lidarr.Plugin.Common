using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Services.Lyrics
{
    /// <summary>
    /// A streaming service's own synced-lyrics source (e.g. Tidal's lyrics endpoint or Apple Music's
    /// SDK lyrics). Implemented per-plugin; the shared <see cref="ILyricsEnricher"/> tries it before
    /// the LRCLIB fallback. Return <c>null</c> when the service has no synced lyrics for the track.
    /// This is the only lyrics code that should remain plugin-local — the fetch-and-write
    /// orchestration lives once in Common.
    /// </summary>
    public interface INativeLyricsSource
    {
        Task<string?> TryGetSyncedLyricsAsync(string artistName, string trackName, string albumName, int durationSeconds, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Shared synced-lyrics enrichment used by every plugin. Tries the optional native source first,
    /// then — when <c>allowLrclibFallback</c> is set — the LRCLIB public API, and writes
    /// the resulting <c>.lrc</c> next to the audio file. Best-effort: it never throws (other than
    /// cancellation) and never writes a file when no lyrics are found, so a lyrics miss can never
    /// fail a download.
    /// </summary>
    public interface ILyricsEnricher : IDisposable
    {
        Task TryEnrichAsync(string audioFilePath, string artistName, string trackName, string albumName, int durationSeconds, bool allowLrclibFallback, CancellationToken cancellationToken = default);
    }
}
