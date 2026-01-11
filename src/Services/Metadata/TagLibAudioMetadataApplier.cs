using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Abstractions.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TagLib;

namespace Lidarr.Plugin.Common.Services.Metadata
{
    internal sealed class TagLibAudioMetadataApplier : IAudioMetadataApplier
    {
        private static readonly Regex GuidPattern = new(
            @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.Compiled);

        private readonly ILogger _logger;

        public TagLibAudioMetadataApplier()
            : this(NullLogger.Instance)
        {
        }

        public TagLibAudioMetadataApplier(ILogger logger)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        public Task ApplyAsync(string filePath, StreamingTrack metadata, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath) || metadata == null)
            {
                return Task.CompletedTask;
            }

            return Task.Run(() =>
            {
                try
                {
                    ApplyMetadataCore(filePath, metadata);
                }
                catch (Exception ex)
                {
                    // Graceful degradation: metadata tagging failures must never fail downloads
                    // Log warning once per file with redacted message (no full stack in info logs)
                    _logger.LogWarning(
                        "[TagLibAudioMetadataApplier] Failed to apply metadata to '{FileName}' (track: {TrackTitle}): {ErrorType}",
                        Path.GetFileName(filePath),
                        metadata.Title ?? "unknown",
                        ex.GetType().Name);
                }
            }, cancellationToken);
        }

        private void ApplyMetadataCore(string filePath, StreamingTrack metadata)
        {
            using var file = TagLib.File.Create(filePath);

            if (!string.IsNullOrEmpty(metadata.Title))
            {
                file.Tag.Title = metadata.Title;
            }

            if (!string.IsNullOrEmpty(metadata.Artist?.Name))
            {
                file.Tag.Performers = new[] { metadata.Artist.Name };
            }

            if (!string.IsNullOrEmpty(metadata.Album?.Artist?.Name))
            {
                file.Tag.AlbumArtists = new[] { metadata.Album.Artist.Name };
            }
            else if (!string.IsNullOrEmpty(metadata.Artist?.Name))
            {
                file.Tag.AlbumArtists = new[] { metadata.Artist.Name };
            }

            if (!string.IsNullOrEmpty(metadata.Album?.Title))
            {
                file.Tag.Album = metadata.Album.Title;
            }

            if (metadata.TrackNumber.HasValue && metadata.TrackNumber.Value > 0)
            {
                file.Tag.Track = (uint)metadata.TrackNumber.Value;
            }

            if (metadata.DiscNumber.HasValue && metadata.DiscNumber.Value > 0)
            {
                file.Tag.Disc = (uint)metadata.DiscNumber.Value;
            }

            if (metadata.Album?.ReleaseDate.HasValue == true)
            {
                file.Tag.Year = (uint)metadata.Album.ReleaseDate.Value.Year;
            }

            if (metadata.Album?.Genres?.Any() == true)
            {
                file.Tag.Genres = new[] { metadata.Album.Genres.First() };
            }

            // ISRC - International Standard Recording Code
            // Normalize: trim whitespace and convert to uppercase (ISO 3901 standard)
            var normalizedIsrc = NormalizeIsrc(metadata.Isrc);
            if (!string.IsNullOrEmpty(normalizedIsrc))
            {
                ApplyIsrc(file, normalizedIsrc);
            }

            // MusicBrainz IDs - universal music database identifiers
            // Normalize: trim and lowercase (canonical UUID format)
            var normalizedTrackMbid = NormalizeMusicBrainzId(metadata.MusicBrainzId);
            if (!string.IsNullOrEmpty(normalizedTrackMbid))
            {
                file.Tag.MusicBrainzTrackId = normalizedTrackMbid;
            }

            var normalizedAlbumMbid = NormalizeMusicBrainzId(metadata.Album?.MusicBrainzId);
            if (!string.IsNullOrEmpty(normalizedAlbumMbid))
            {
                file.Tag.MusicBrainzReleaseId = normalizedAlbumMbid;
            }

            file.Save();
        }

        /// <summary>
        /// Normalizes ISRC to ISO 3901 standard format: uppercase, no whitespace.
        /// </summary>
        private static string? NormalizeIsrc(string? isrc)
        {
            if (string.IsNullOrWhiteSpace(isrc))
            {
                return null;
            }

            return isrc.Trim().ToUpperInvariant();
        }

        /// <summary>
        /// Normalizes MusicBrainz ID to canonical UUID format: lowercase, trimmed.
        /// Returns null if the value is not a valid UUID (garbage preservation is not normalization).
        /// </summary>
        private static string? NormalizeMusicBrainzId(string? mbid)
        {
            if (string.IsNullOrWhiteSpace(mbid))
            {
                return null;
            }

            var trimmed = mbid.Trim();

            // Validate UUID format - don't write garbage to tags
            if (!GuidPattern.IsMatch(trimmed))
            {
                return null;
            }

            return trimmed.ToLowerInvariant();
        }

        /// <summary>
        /// Applies ISRC to the audio file using format-specific tagging.
        /// ID3v2: TSRC frame
        /// Vorbis/FLAC/Ogg: ISRC comment
        /// MP4/M4A: Best-effort via iTunes custom box (not all TagLib builds support this)
        /// </summary>
        private void ApplyIsrc(TagLib.File file, string isrc)
        {
            // Try ID3v2 tag (MP3)
            if (file.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3v2Tag)
            {
                // TSRC is the standard ID3v2 frame for ISRC
                var tsrcFrame = TagLib.Id3v2.TextInformationFrame.Get(
                    id3v2Tag,
                    TagLib.ByteVector.FromString("TSRC", TagLib.StringType.Latin1),
                    true);
                tsrcFrame.Text = new[] { isrc };
                return;
            }

            // Try Xiph/Vorbis comment (FLAC, Ogg Vorbis)
            if (file.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiphComment)
            {
                xiphComment.SetField("ISRC", isrc);
                return;
            }

            // Try Apple tag (M4A, AAC) - best effort, may not persist in all scenarios
            try
            {
                if (file.GetTag(TagTypes.Apple) is TagLib.Mpeg4.AppleTag appleTag)
                {
                    appleTag.SetDashBox("com.apple.iTunes", "ISRC", isrc);
                }
            }
            catch (Exception ex)
            {
                // M4A ISRC tagging is best-effort; log at debug level and continue
                _logger.LogDebug("M4A ISRC tagging failed (best-effort): {ErrorType}", ex.GetType().Name);
            }
        }
    }
}
