using System;
using System.Text;

namespace Lidarr.Plugin.Common.HostBridge;

/// <summary>
/// Builds the three <c>ReleaseInfo</c> string fields — <c>Guid</c>, <c>DownloadUrl</c>, and
/// <c>Title</c> — that every RicherTunes streaming-service plugin constructs from the same
/// album + quality + artist data.
///
/// <para>Why strings instead of a <c>ReleaseInfo</c> instance?  Common cannot reference
/// <c>NzbDrone.Core.Indexers.ReleaseInfo</c> (host assembly). Returning the three strings lets
/// each plugin map to its own <c>ReleaseInfo</c> / <c>IndexerRelease</c> type cleanly.</para>
///
/// <para><strong>Title format</strong> (default):</para>
/// <code>
///   {Artist} - {Album} ({Year}) [{FormatMarker}] [{ExtraMarker}] [{ReleaseGroup}]   // all fields
///   {Artist} - {Album} ({Year}) [{FormatMarker}] [{ReleaseGroup}]   // with year and format marker, no extra
///   {Artist} - {Album} ({Year}) [{ReleaseGroup}]                    // with year, no format marker
///   {Artist} - {Album} [{FormatMarker}] [{ReleaseGroup}]            // no year, with format marker
///   {Artist} - {Album} [{ReleaseGroup}]                             // no year, no format marker
/// </code>
///
/// <para><strong>GUID grammar</strong> (aligns with <see cref="PrefixedReleaseGuidParser"/>):</para>
/// <code>
///   {scheme}:album:{albumId}                        // no quality hint
///   {scheme}:album:{albumId}:{qualityHint}          // with quality hint
/// </code>
///
/// <para><strong>DownloadUrl format</strong>:</para>
/// <code>
///   {scheme}://album/{albumId}                      // no quality hint
///   {scheme}://album/{albumId}?quality={qualityHint} // with quality hint
/// </code>
///
/// <para>This is Wave A item 8 from the May 2026 bridge-unification plan — the
/// Guid/DownloadUrl/Title boilerplate was duplicated ~40 LOC per plugin.</para>
/// </summary>
public sealed class AlbumReleaseInfoBuilder
{
    private string? _artist;
    private string? _album;
    private int? _year;
    private string? _editionMarker;
    private bool _explicitMarker;
    private bool _liveMarker;
    private string? _formatMarker;
    private string? _extraMarker;
    private string _releaseGroup = "WEB";
    private string? _scheme;
    private string? _albumId;
    private string? _qualityHint;

    /// <summary>Artist display name. Required.</summary>
    public AlbumReleaseInfoBuilder WithArtist(string artist)
    {
        _artist = artist;
        return this;
    }

    /// <summary>Album title. Required.</summary>
    public AlbumReleaseInfoBuilder WithAlbum(string album)
    {
        _album = album;
        return this;
    }

    /// <summary>
    /// Release year. When null or &lt;= 0, the year parentheses are omitted from the title.
    /// </summary>
    public AlbumReleaseInfoBuilder WithYear(int? year)
    {
        _year = year;
        return this;
    }

    /// <summary>
    /// Optional edition bracket inserted between <c>({Year})</c> and the format bracket.
    /// Used for variant labels like <c>Deluxe</c>, <c>Anniversary Edition</c>,
    /// <c>Remastered</c>, etc. When null, empty, or whitespace, the bracket is omitted.
    /// </summary>
    public AlbumReleaseInfoBuilder WithEditionMarker(string? edition)
    {
        _editionMarker = edition;
        return this;
    }

    /// <summary>
    /// Toggle for an <c>[Explicit]</c> bracket inserted between <see cref="WithEditionMarker"/>
    /// and the format bracket. When true, the literal <c>[Explicit]</c> string is appended;
    /// when false the bracket is omitted.
    /// </summary>
    public AlbumReleaseInfoBuilder WithExplicitMarker(bool isExplicit)
    {
        _explicitMarker = isExplicit;
        return this;
    }

    /// <summary>
    /// Toggle for a <c>[LIVE]</c> bracket inserted between <see cref="WithExplicitMarker"/>
    /// and the format bracket. When true, the literal <c>[LIVE]</c> string is appended;
    /// when false the bracket is omitted.
    /// </summary>
    public AlbumReleaseInfoBuilder WithLiveMarker(bool isLive)
    {
        _liveMarker = isLive;
        return this;
    }

    /// <summary>
    /// Optional format bracket inserted before the release-group bracket, e.g. <c>FLAC</c>,
    /// <c>MP3 320</c>, <c>Hi-Res</c>. When null or empty, the bracket is omitted entirely
    /// (Apple Music behaviour — no format marker in the title).
    /// </summary>
    public AlbumReleaseInfoBuilder WithFormatMarker(string? marker)
    {
        _formatMarker = marker;
        return this;
    }

    /// <summary>
    /// Optional second bracket between <see cref="WithFormatMarker"/> and
    /// <see cref="WithReleaseGroup"/>. Tidal uses this for quality sub-type tokens such as
    /// <c>HIRES</c>, <c>320</c>, <c>96</c> — producing e.g. <c>[FLAC] [HIRES] [WEB]</c>.
    /// When null or empty, the bracket is omitted.
    /// </summary>
    public AlbumReleaseInfoBuilder WithExtraMarker(string? extra)
    {
        _extraMarker = extra;
        return this;
    }

    /// <summary>
    /// Release-group tag inserted as the last title bracket. Defaults to <c>WEB</c>.
    /// </summary>
    public AlbumReleaseInfoBuilder WithReleaseGroup(string group)
    {
        _releaseGroup = group;
        return this;
    }

    /// <summary>
    /// Plugin scheme literal: <c>tidal</c>, <c>applemusic</c>, <c>qobuz</c>. Required.
    /// Used as the GUID prefix and URL scheme.
    /// </summary>
    public AlbumReleaseInfoBuilder WithScheme(string scheme)
    {
        _scheme = scheme;
        return this;
    }

    /// <summary>
    /// Service-specific album identifier. Required.
    /// </summary>
    public AlbumReleaseInfoBuilder WithAlbumId(string id)
    {
        _albumId = id;
        return this;
    }

    /// <summary>
    /// Optional per-release quality token (e.g. <c>Lossless</c>, <c>HiRes</c>).
    /// When non-null/non-empty it is appended to the GUID as a fourth colon segment and
    /// to the DownloadUrl as a <c>?quality=</c> query parameter — allowing multiple
    /// quality releases for the same album to have distinct, stable identifiers.
    /// </summary>
    public AlbumReleaseInfoBuilder WithQualityHint(string? hint)
    {
        _qualityHint = hint;
        return this;
    }

    /// <summary>
    /// Validate required fields and produce <c>(Guid, DownloadUrl, Title)</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when any required field (<see cref="WithArtist"/>, <see cref="WithAlbum"/>,
    /// <see cref="WithScheme"/>, <see cref="WithAlbumId"/>) has not been set.
    /// </exception>
    public (string Guid, string DownloadUrl, string Title) Build()
    {
        if (string.IsNullOrWhiteSpace(_artist))
            throw new InvalidOperationException("Artist must be set via WithArtist().");
        if (string.IsNullOrWhiteSpace(_album))
            throw new InvalidOperationException("Album must be set via WithAlbum().");
        if (string.IsNullOrWhiteSpace(_scheme))
            throw new InvalidOperationException("Scheme must be set via WithScheme().");
        if (string.IsNullOrWhiteSpace(_albumId))
            throw new InvalidOperationException("AlbumId must be set via WithAlbumId().");

        var guid = BuildGuid();
        var downloadUrl = BuildDownloadUrl();
        var title = BuildTitle();

        return (guid, downloadUrl, title);
    }

    private string BuildGuid()
    {
        // Grammar: {scheme}:album:{albumId}[:{qualityHint}]
        var sb = new StringBuilder();
        sb.Append(_scheme);
        sb.Append(":album:");
        sb.Append(_albumId);
        if (!string.IsNullOrWhiteSpace(_qualityHint))
        {
            sb.Append(':');
            sb.Append(_qualityHint);
        }
        return sb.ToString();
    }

    private string BuildDownloadUrl()
    {
        // Format: {scheme}://album/{albumId}[?quality={qualityHint}]
        var sb = new StringBuilder();
        sb.Append(_scheme);
        sb.Append("://album/");
        sb.Append(_albumId);
        if (!string.IsNullOrWhiteSpace(_qualityHint))
        {
            sb.Append("?quality=");
            sb.Append(Uri.EscapeDataString(_qualityHint!));
        }
        return sb.ToString();
    }

    private string BuildTitle()
    {
        var sb = new StringBuilder();
        sb.Append(_artist);
        sb.Append(" - ");
        sb.Append(_album);

        var hasYear = _year.HasValue && _year.Value > 0;
        if (hasYear)
        {
            sb.Append(" (");
            sb.Append(_year!.Value);
            sb.Append(')');
        }

        // Wave 19D: optional brackets between Year and Format, in canonical order
        // (Edition, Explicit, Live). Qobuzarr's TitleGenerator emits this shape;
        // tidalarr/apple don't set them so their titles are unaffected.
        if (!string.IsNullOrWhiteSpace(_editionMarker))
        {
            sb.Append(" [");
            sb.Append(_editionMarker);
            sb.Append(']');
        }
        if (_explicitMarker)
        {
            sb.Append(" [Explicit]");
        }
        if (_liveMarker)
        {
            sb.Append(" [LIVE]");
        }

        if (!string.IsNullOrWhiteSpace(_formatMarker))
        {
            sb.Append(" [");
            sb.Append(_formatMarker);
            sb.Append(']');
        }

        if (!string.IsNullOrWhiteSpace(_extraMarker))
        {
            sb.Append(" [");
            sb.Append(_extraMarker);
            sb.Append(']');
        }

        sb.Append(" [");
        sb.Append(_releaseGroup);
        sb.Append(']');

        return sb.ToString();
    }
}
