using System;
using System.Collections.Generic;

namespace Lidarr.Plugin.Common.HostBridge;

/// <summary>
/// One quality variant produced by <see cref="MultiQualityReleaseBuilder"/>: the neutral
/// <c>ReleaseInfo</c> string fields plus the estimated size. Common cannot reference the host
/// <c>NzbDrone.Core.Indexers.ReleaseInfo</c> type, so the plugin maps each row onto its own
/// release object (setting <c>Guid</c>, <c>DownloadUrl</c>, <c>Title</c>, <c>Size</c>).
/// </summary>
/// <param name="QualityHint">The quality token that disambiguates this release (e.g. "Lossless", "FLAC").</param>
/// <param name="Guid">Stable per-quality GUID from <see cref="AlbumReleaseInfoBuilder"/>.</param>
/// <param name="DownloadUrl">Per-quality download URL from <see cref="AlbumReleaseInfoBuilder"/>.</param>
/// <param name="Title">Release title from <see cref="AlbumReleaseInfoBuilder"/>.</param>
/// <param name="SizeBytes">Estimated size from <see cref="AlbumSizeEstimator"/>.</param>
public sealed record MultiQualityRelease(
    string QualityHint,
    string Guid,
    string DownloadUrl,
    string Title,
    long SizeBytes);

/// <summary>
/// Builds one release per available quality for a single album — the pattern every streaming
/// plugin's indexer emits so Lidarr shows the user all quality options (tidalarr's
/// <c>ConvertToReleaseInfosStatic</c>, qobuzarr's per-quality parser loop). It composes the
/// existing <see cref="AlbumReleaseInfoBuilder"/> (called once per tier, with the album-level
/// fields held constant and the per-tier format/extra/quality tokens varied) and
/// <see cref="AlbumSizeEstimator"/> (one size per tier), so the only thing a plugin supplies is
/// its own list of quality specs via <see cref="AddQuality"/>.
///
/// <para>Album-level markers (edition / explicit / live) are set once and applied to every tier.
/// Tidal leaves them unset; qobuz pre-computes them from its metadata and sets them here.</para>
/// </summary>
public sealed class MultiQualityReleaseBuilder
{
    private readonly List<QualitySpec> _qualities = new();

    private string? _artist;
    private string? _album;
    private int? _year;
    private string? _scheme;
    private string? _albumId;
    private string? _releaseGroup;
    private string? _editionMarker;
    private bool _explicitMarker;
    private bool _liveMarker;
    private double _durationSeconds;
    private long _minimumSizeBytes;

    private readonly record struct QualitySpec(string QualityHint, string? FormatMarker, string? ExtraMarker, double BitsPerSecond);

    /// <summary>Artist display name. Required.</summary>
    public MultiQualityReleaseBuilder WithArtist(string artist)
    {
        _artist = artist;
        return this;
    }

    /// <summary>Album title. Required.</summary>
    public MultiQualityReleaseBuilder WithAlbum(string album)
    {
        _album = album;
        return this;
    }

    /// <summary>Release year. When null or ≤ 0 the year parentheses are omitted from titles.</summary>
    public MultiQualityReleaseBuilder WithYear(int? year)
    {
        _year = year;
        return this;
    }

    /// <summary>Plugin scheme literal (<c>tidal</c>, <c>qobuz</c>, …). Required — GUID prefix + URL scheme.</summary>
    public MultiQualityReleaseBuilder WithScheme(string scheme)
    {
        _scheme = scheme;
        return this;
    }

    /// <summary>Service-specific album identifier. Required.</summary>
    public MultiQualityReleaseBuilder WithAlbumId(string albumId)
    {
        _albumId = albumId;
        return this;
    }

    /// <summary>Release-group tag (last title bracket). When unset, <see cref="AlbumReleaseInfoBuilder"/>'s default (<c>WEB</c>) is used.</summary>
    public MultiQualityReleaseBuilder WithReleaseGroup(string group)
    {
        _releaseGroup = group;
        return this;
    }

    /// <summary>Optional edition bracket applied to every tier (e.g. <c>Deluxe</c>, <c>Remastered</c>).</summary>
    public MultiQualityReleaseBuilder WithEditionMarker(string? edition)
    {
        _editionMarker = edition;
        return this;
    }

    /// <summary>When true, an <c>[Explicit]</c> bracket is applied to every tier.</summary>
    public MultiQualityReleaseBuilder WithExplicitMarker(bool isExplicit)
    {
        _explicitMarker = isExplicit;
        return this;
    }

    /// <summary>When true, a <c>[LIVE]</c> bracket is applied to every tier.</summary>
    public MultiQualityReleaseBuilder WithLiveMarker(bool isLive)
    {
        _liveMarker = isLive;
        return this;
    }

    /// <summary>
    /// Album playback duration in seconds, used by <see cref="AlbumSizeEstimator"/> for every tier.
    /// When ≤ 0, sizes fall back to <see cref="WithMinimumSizeBytes"/> (or 0).
    /// </summary>
    public MultiQualityReleaseBuilder WithDurationSeconds(double durationSeconds)
    {
        _durationSeconds = durationSeconds;
        return this;
    }

    /// <summary>Lower bound applied to every tier's estimated size (e.g. <see cref="AlbumSizeEstimator.DefaultMinimumSizeBytes"/>).</summary>
    public MultiQualityReleaseBuilder WithMinimumSizeBytes(long minimumBytes)
    {
        _minimumSizeBytes = minimumBytes;
        return this;
    }

    /// <summary>
    /// Add one quality tier. Call once per quality the album is offered in.
    /// </summary>
    /// <param name="qualityHint">
    /// Quality token appended to the GUID/URL so each tier has a distinct, stable identifier
    /// (e.g. "Lossless", "HiRes", "FLAC"). Required.
    /// </param>
    /// <param name="formatMarker">Title format bracket (e.g. <c>FLAC</c>, <c>AAC</c>, <c>MP3 320kbps</c>). Optional.</param>
    /// <param name="extraMarker">Second title bracket (e.g. <c>HIRES</c>, <c>320</c>). Optional.</param>
    /// <param name="bitsPerSecond">Encoded bitrate in bits per second for size estimation (see <see cref="AlbumSizeEstimator"/>).</param>
    public MultiQualityReleaseBuilder AddQuality(string qualityHint, string? formatMarker, string? extraMarker, double bitsPerSecond)
    {
        if (string.IsNullOrWhiteSpace(qualityHint))
        {
            throw new ArgumentException("Quality hint must be non-empty.", nameof(qualityHint));
        }

        _qualities.Add(new QualitySpec(qualityHint, formatMarker, extraMarker, bitsPerSecond));
        return this;
    }

    /// <summary>
    /// Produce one <see cref="MultiQualityRelease"/> per added quality, in insertion order.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no qualities were added. Required album fields are validated by the underlying
    /// <see cref="AlbumReleaseInfoBuilder"/>.
    /// </exception>
    public IReadOnlyList<MultiQualityRelease> Build()
    {
        if (_qualities.Count == 0)
        {
            throw new InvalidOperationException("At least one quality must be added via AddQuality().");
        }

        var results = new List<MultiQualityRelease>(_qualities.Count);
        foreach (var q in _qualities)
        {
            var builder = new AlbumReleaseInfoBuilder()
                .WithArtist(_artist!)
                .WithAlbum(_album!)
                .WithYear(_year)
                .WithEditionMarker(_editionMarker)
                .WithExplicitMarker(_explicitMarker)
                .WithLiveMarker(_liveMarker)
                .WithFormatMarker(q.FormatMarker)
                .WithExtraMarker(q.ExtraMarker)
                .WithScheme(_scheme!)
                .WithAlbumId(_albumId!)
                .WithQualityHint(q.QualityHint);

            if (!string.IsNullOrWhiteSpace(_releaseGroup))
            {
                builder.WithReleaseGroup(_releaseGroup!);
            }

            var (guid, downloadUrl, title) = builder.Build();
            var size = AlbumSizeEstimator.EstimateBytesFromBitrate(_durationSeconds, q.BitsPerSecond, _minimumSizeBytes);

            results.Add(new MultiQualityRelease(q.QualityHint, guid, downloadUrl, title, size));
        }

        return results;
    }
}
