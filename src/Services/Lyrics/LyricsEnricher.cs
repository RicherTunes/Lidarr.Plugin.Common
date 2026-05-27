using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Lyrics;

/// <summary>
/// Best-effort synced-lyrics (.lrc) enricher. Fetches lyrics from LRCLIB
/// and saves alongside the audio file. Failures never propagate — the
/// download succeeds regardless of lyrics availability.
/// </summary>
public sealed class LyricsEnricher : IDisposable
{
    private readonly LrclibClient _client;
    private readonly ILogger? _logger;

    public LyricsEnricher(ILogger? logger = null)
    {
        _client = new LrclibClient();
        _logger = logger;
    }

    public LyricsEnricher(LrclibClient client, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger;
    }

    /// <summary>
    /// Fetch synced lyrics and save as .lrc alongside the audio file.
    /// No-ops silently when artist/track is empty or lyrics aren't available.
    /// </summary>
    public async Task TryEnrichAsync(
        string audioFilePath,
        string artistName,
        string trackName,
        string albumName,
        int durationSeconds,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artistName) || string.IsNullOrWhiteSpace(trackName))
            return;

        try
        {
            var lyrics = await _client.TryFetchSyncedLyricsAsync(
                artistName, trackName, albumName, durationSeconds, ct).ConfigureAwait(false);
            if (lyrics is null) return;

            var lrcPath = Path.ChangeExtension(audioFilePath, ".lrc");
            await File.WriteAllTextAsync(lrcPath, lyrics, ct).ConfigureAwait(false);
            _logger?.LogDebug("Saved synced lyrics: {File}", Path.GetFileName(lrcPath));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Lyrics fetch failed for {Artist} — {Track} (non-fatal)", artistName, trackName);
        }
    }

    public void Dispose() => _client.Dispose();
}
