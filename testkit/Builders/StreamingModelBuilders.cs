using System;
using System.Collections.Generic;
using Lidarr.Plugin.Abstractions.Models;

namespace Lidarr.Plugin.Common.TestKit.Builders;

/// <summary>
/// Fluent builder for creating <see cref="StreamingArtist"/> instances in tests.
/// </summary>
public sealed class StreamingArtistBuilder
{
    private string _id = Guid.NewGuid().ToString();
    private string _name = "Test Artist";
    private string _biography = string.Empty;
    private readonly List<string> _genres = new();
    private string _country = string.Empty;
    private readonly Dictionary<string, string> _imageUrls = new();
    private readonly Dictionary<string, string> _externalUrls = new();
    private readonly Dictionary<string, object> _metadata = new();

    public StreamingArtistBuilder WithId(string id)
    {
        _id = id;
        return this;
    }

    public StreamingArtistBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public StreamingArtistBuilder WithBiography(string biography)
    {
        _biography = biography;
        return this;
    }

    public StreamingArtistBuilder WithGenre(string genre)
    {
        _genres.Add(genre);
        return this;
    }

    public StreamingArtistBuilder WithGenres(params string[] genres)
    {
        _genres.AddRange(genres);
        return this;
    }

    public StreamingArtistBuilder WithCountry(string country)
    {
        _country = country;
        return this;
    }

    public StreamingArtistBuilder WithImageUrl(string size, string url)
    {
        _imageUrls[size] = url;
        return this;
    }

    public StreamingArtistBuilder WithExternalUrl(string service, string url)
    {
        _externalUrls[service] = url;
        return this;
    }

    public StreamingArtistBuilder WithMetadata(string key, object value)
    {
        _metadata[key] = value;
        return this;
    }

    public StreamingArtist Build() => new()
    {
        Id = _id,
        Name = _name,
        Biography = _biography,
        Genres = new List<string>(_genres),
        Country = _country,
        ImageUrls = new Dictionary<string, string>(_imageUrls),
        ExternalUrls = new Dictionary<string, string>(_externalUrls),
        Metadata = new Dictionary<string, object>(_metadata)
    };

    /// <summary>
    /// Creates a minimal artist for quick tests.
    /// </summary>
    public static StreamingArtist CreateMinimal(string name = "Test Artist", string? id = null) =>
        new StreamingArtistBuilder()
            .WithId(id ?? Guid.NewGuid().ToString())
            .WithName(name)
            .Build();

    /// <summary>
    /// Creates a fully populated artist for comprehensive tests.
    /// </summary>
    public static StreamingArtist CreateComplete(string name = "Complete Artist") =>
        new StreamingArtistBuilder()
            .WithName(name)
            .WithBiography("A comprehensive test artist biography.")
            .WithGenres("Rock", "Alternative", "Indie")
            .WithCountry("US")
            .WithImageUrl("small", "https://example.com/artist-small.jpg")
            .WithImageUrl("medium", "https://example.com/artist-medium.jpg")
            .WithImageUrl("large", "https://example.com/artist-large.jpg")
            .WithExternalUrl("spotify", "https://open.spotify.com/artist/123")
            .WithExternalUrl("musicbrainz", "https://musicbrainz.org/artist/456")
            .WithMetadata("popularity", 85)
            .WithMetadata("followers", 1000000)
            .Build();
}

/// <summary>
/// Fluent builder for creating <see cref="StreamingAlbum"/> instances in tests.
/// </summary>
public sealed class StreamingAlbumBuilder
{
    private string _id = Guid.NewGuid().ToString();
    private string _title = "Test Album";
    private StreamingArtist _artist = StreamingArtistBuilder.CreateMinimal();
    private readonly List<StreamingArtist> _additionalArtists = new();
    private DateTime? _releaseDate = DateTime.Today;
    private StreamingAlbumType _type = StreamingAlbumType.Album;
    private int _trackCount = 10;
    private TimeSpan? _duration = TimeSpan.FromMinutes(45);
    private readonly List<string> _genres = new();
    private string _label = string.Empty;
    private string _upc = string.Empty;
    private string _musicBrainzId = string.Empty;
    private readonly List<StreamingQuality> _availableQualities = new();
    private readonly Dictionary<string, string> _coverArtUrls = new();
    private readonly Dictionary<string, string> _externalUrls = new();
    private readonly Dictionary<string, string> _externalIds = new();
    private readonly Dictionary<string, object> _metadata = new();

    public StreamingAlbumBuilder WithId(string id)
    {
        _id = id;
        return this;
    }

    public StreamingAlbumBuilder WithTitle(string title)
    {
        _title = title;
        return this;
    }

    public StreamingAlbumBuilder WithArtist(StreamingArtist artist)
    {
        _artist = artist;
        return this;
    }

    public StreamingAlbumBuilder WithArtist(string name)
    {
        _artist = StreamingArtistBuilder.CreateMinimal(name);
        return this;
    }

    public StreamingAlbumBuilder WithAdditionalArtist(StreamingArtist artist)
    {
        _additionalArtists.Add(artist);
        return this;
    }

    public StreamingAlbumBuilder WithReleaseDate(DateTime? date)
    {
        _releaseDate = date;
        return this;
    }

    public StreamingAlbumBuilder WithType(StreamingAlbumType type)
    {
        _type = type;
        return this;
    }

    public StreamingAlbumBuilder WithTrackCount(int count)
    {
        _trackCount = count;
        return this;
    }

    public StreamingAlbumBuilder WithDuration(TimeSpan? duration)
    {
        _duration = duration;
        return this;
    }

    public StreamingAlbumBuilder WithGenre(string genre)
    {
        _genres.Add(genre);
        return this;
    }

    public StreamingAlbumBuilder WithGenres(params string[] genres)
    {
        _genres.AddRange(genres);
        return this;
    }

    public StreamingAlbumBuilder WithLabel(string label)
    {
        _label = label;
        return this;
    }

    public StreamingAlbumBuilder WithUpc(string upc)
    {
        _upc = upc;
        return this;
    }

    public StreamingAlbumBuilder WithMusicBrainzId(string mbid)
    {
        _musicBrainzId = mbid;
        return this;
    }

    public StreamingAlbumBuilder WithQuality(StreamingQuality quality)
    {
        _availableQualities.Add(quality);
        return this;
    }

    public StreamingAlbumBuilder WithCoverArtUrl(string size, string url)
    {
        _coverArtUrls[size] = url;
        return this;
    }

    public StreamingAlbumBuilder WithExternalUrl(string service, string url)
    {
        _externalUrls[service] = url;
        return this;
    }

    public StreamingAlbumBuilder WithExternalId(string service, string id)
    {
        _externalIds[service] = id;
        return this;
    }

    public StreamingAlbumBuilder WithMetadata(string key, object value)
    {
        _metadata[key] = value;
        return this;
    }

    public StreamingAlbum Build()
    {
        var album = new StreamingAlbum
        {
            Id = _id,
            Title = _title,
            Artist = _artist,
            AdditionalArtists = new List<StreamingArtist>(_additionalArtists),
            ReleaseDate = _releaseDate,
            Type = _type,
            TrackCount = _trackCount,
            Duration = _duration,
            Genres = new List<string>(_genres),
            Label = _label,
            Upc = _upc,
            MusicBrainzId = _musicBrainzId,
            AvailableQualities = new List<StreamingQuality>(_availableQualities),
            CoverArtUrls = new Dictionary<string, string>(_coverArtUrls),
            ExternalUrls = new Dictionary<string, string>(_externalUrls),
            Metadata = new Dictionary<string, object>(_metadata)
        };

        foreach (var kvp in _externalIds)
        {
            album.ExternalIds[kvp.Key] = kvp.Value;
        }

        return album;
    }

    /// <summary>
    /// Creates a minimal album for quick tests.
    /// </summary>
    public static StreamingAlbum CreateMinimal(string title = "Test Album", string artistName = "Test Artist") =>
        new StreamingAlbumBuilder()
            .WithTitle(title)
            .WithArtist(artistName)
            .Build();

    /// <summary>
    /// Creates a lossless album with high-quality settings.
    /// </summary>
    public static StreamingAlbum CreateLossless(string title = "Lossless Album") =>
        new StreamingAlbumBuilder()
            .WithTitle(title)
            .WithQuality(StreamingQualityBuilder.CreateFlacCd())
            .WithQuality(StreamingQualityBuilder.CreateFlacHiRes())
            .Build();

    /// <summary>
    /// Creates a fully populated album for comprehensive tests.
    /// </summary>
    public static StreamingAlbum CreateComplete(string title = "Complete Album") =>
        new StreamingAlbumBuilder()
            .WithTitle(title)
            .WithArtist(StreamingArtistBuilder.CreateComplete())
            .WithReleaseDate(new DateTime(2024, 1, 15))
            .WithType(StreamingAlbumType.Album)
            .WithTrackCount(12)
            .WithDuration(TimeSpan.FromMinutes(52))
            .WithGenres("Rock", "Alternative")
            .WithLabel("Test Records")
            .WithUpc("012345678901")
            .WithMusicBrainzId("12345678-1234-1234-1234-123456789012")
            .WithQuality(StreamingQualityBuilder.CreateMp3320())
            .WithQuality(StreamingQualityBuilder.CreateFlacCd())
            .WithCoverArtUrl("small", "https://example.com/cover-small.jpg")
            .WithCoverArtUrl("large", "https://example.com/cover-large.jpg")
            .WithExternalId("tidal", "tidal-123")
            .WithExternalId("qobuz", "qobuz-456")
            .Build();
}

/// <summary>
/// Fluent builder for creating <see cref="StreamingTrack"/> instances in tests.
/// </summary>
public sealed class StreamingTrackBuilder
{
    private string _id = Guid.NewGuid().ToString();
    private string _title = "Test Track";
    private StreamingArtist _artist = StreamingArtistBuilder.CreateMinimal();
    private StreamingAlbum _album = StreamingAlbumBuilder.CreateMinimal();
    private int? _trackNumber = 1;
    private int? _discNumber = 1;
    private TimeSpan? _duration = TimeSpan.FromMinutes(3.5);
    private bool _isExplicit;
    private string _isrc = string.Empty;
    private string _musicBrainzId = string.Empty;
    private readonly List<StreamingArtist> _featuredArtists = new();
    private readonly List<StreamingQuality> _availableQualities = new();
    private string _previewUrl = string.Empty;
    private long? _popularity;
    private readonly Dictionary<string, string> _externalIds = new();
    private readonly Dictionary<string, object> _metadata = new();

    public StreamingTrackBuilder WithId(string id)
    {
        _id = id;
        return this;
    }

    public StreamingTrackBuilder WithTitle(string title)
    {
        _title = title;
        return this;
    }

    public StreamingTrackBuilder WithArtist(StreamingArtist artist)
    {
        _artist = artist;
        return this;
    }

    public StreamingTrackBuilder WithArtist(string name)
    {
        _artist = StreamingArtistBuilder.CreateMinimal(name);
        return this;
    }

    public StreamingTrackBuilder WithAlbum(StreamingAlbum album)
    {
        _album = album;
        return this;
    }

    public StreamingTrackBuilder WithTrackNumber(int? number)
    {
        _trackNumber = number;
        return this;
    }

    public StreamingTrackBuilder WithDiscNumber(int? number)
    {
        _discNumber = number;
        return this;
    }

    public StreamingTrackBuilder WithDuration(TimeSpan? duration)
    {
        _duration = duration;
        return this;
    }

    public StreamingTrackBuilder AsExplicit(bool isExplicit = true)
    {
        _isExplicit = isExplicit;
        return this;
    }

    public StreamingTrackBuilder WithIsrc(string isrc)
    {
        _isrc = isrc;
        return this;
    }

    public StreamingTrackBuilder WithMusicBrainzId(string mbid)
    {
        _musicBrainzId = mbid;
        return this;
    }

    public StreamingTrackBuilder WithFeaturedArtist(StreamingArtist artist)
    {
        _featuredArtists.Add(artist);
        return this;
    }

    public StreamingTrackBuilder WithFeaturedArtist(string name)
    {
        _featuredArtists.Add(StreamingArtistBuilder.CreateMinimal(name));
        return this;
    }

    public StreamingTrackBuilder WithQuality(StreamingQuality quality)
    {
        _availableQualities.Add(quality);
        return this;
    }

    public StreamingTrackBuilder WithPreviewUrl(string url)
    {
        _previewUrl = url;
        return this;
    }

    public StreamingTrackBuilder WithPopularity(long? popularity)
    {
        _popularity = popularity;
        return this;
    }

    public StreamingTrackBuilder WithExternalId(string service, string id)
    {
        _externalIds[service] = id;
        return this;
    }

    public StreamingTrackBuilder WithMetadata(string key, object value)
    {
        _metadata[key] = value;
        return this;
    }

    public StreamingTrack Build()
    {
        var track = new StreamingTrack
        {
            Id = _id,
            Title = _title,
            Artist = _artist,
            Album = _album,
            TrackNumber = _trackNumber,
            DiscNumber = _discNumber,
            Duration = _duration,
            IsExplicit = _isExplicit,
            Isrc = _isrc,
            MusicBrainzId = _musicBrainzId,
            FeaturedArtists = new List<StreamingArtist>(_featuredArtists),
            AvailableQualities = new List<StreamingQuality>(_availableQualities),
            PreviewUrl = _previewUrl,
            Popularity = _popularity,
            Metadata = new Dictionary<string, object>(_metadata)
        };

        foreach (var kvp in _externalIds)
        {
            track.ExternalIds[kvp.Key] = kvp.Value;
        }

        return track;
    }

    /// <summary>
    /// Creates a minimal track for quick tests.
    /// </summary>
    public static StreamingTrack CreateMinimal(string title = "Test Track") =>
        new StreamingTrackBuilder()
            .WithTitle(title)
            .Build();

    /// <summary>
    /// Creates a track with featured artists.
    /// </summary>
    public static StreamingTrack CreateWithFeatures(string title, params string[] featureNames)
    {
        var builder = new StreamingTrackBuilder().WithTitle(title);
        foreach (var name in featureNames)
        {
            builder.WithFeaturedArtist(name);
        }
        return builder.Build();
    }
}

/// <summary>
/// Fluent builder for creating <see cref="StreamingQuality"/> instances in tests.
/// </summary>
public sealed class StreamingQualityBuilder
{
    private string _id = "quality-id";
    private string _name = "Test Quality";
    private string _format = "FLAC";
    private int? _bitrate;
    private int? _sampleRate = 44100;
    private int? _bitDepth = 16;

    public StreamingQualityBuilder WithId(string id)
    {
        _id = id;
        return this;
    }

    public StreamingQualityBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public StreamingQualityBuilder WithFormat(string format)
    {
        _format = format;
        return this;
    }

    public StreamingQualityBuilder WithBitrate(int? bitrate)
    {
        _bitrate = bitrate;
        return this;
    }

    public StreamingQualityBuilder WithSampleRate(int? sampleRate)
    {
        _sampleRate = sampleRate;
        return this;
    }

    public StreamingQualityBuilder WithBitDepth(int? bitDepth)
    {
        _bitDepth = bitDepth;
        return this;
    }

    public StreamingQuality Build() => new()
    {
        Id = _id,
        Name = _name,
        Format = _format,
        Bitrate = _bitrate,
        SampleRate = _sampleRate,
        BitDepth = _bitDepth
    };

    /// <summary>Creates MP3 320kbps quality.</summary>
    public static StreamingQuality CreateMp3320() =>
        new StreamingQualityBuilder()
            .WithId("mp3-320")
            .WithName("MP3 320")
            .WithFormat("MP3")
            .WithBitrate(320)
            .WithSampleRate(null)
            .WithBitDepth(null)
            .Build();

    /// <summary>Creates AAC 256kbps quality.</summary>
    public static StreamingQuality CreateAac256() =>
        new StreamingQualityBuilder()
            .WithId("aac-256")
            .WithName("AAC 256")
            .WithFormat("AAC")
            .WithBitrate(256)
            .WithSampleRate(null)
            .WithBitDepth(null)
            .Build();

    /// <summary>Creates FLAC CD quality (44.1kHz/16bit).</summary>
    public static StreamingQuality CreateFlacCd() =>
        new StreamingQualityBuilder()
            .WithId("flac-cd")
            .WithName("FLAC CD")
            .WithFormat("FLAC")
            .WithSampleRate(44100)
            .WithBitDepth(16)
            .Build();

    /// <summary>Creates FLAC Hi-Res quality (96kHz/24bit).</summary>
    public static StreamingQuality CreateFlacHiRes() =>
        new StreamingQualityBuilder()
            .WithId("flac-hires")
            .WithName("FLAC Hi-Res")
            .WithFormat("FLAC")
            .WithSampleRate(96000)
            .WithBitDepth(24)
            .Build();

    /// <summary>Creates FLAC Ultra Hi-Res quality (192kHz/24bit).</summary>
    public static StreamingQuality CreateFlacUltraHiRes() =>
        new StreamingQualityBuilder()
            .WithId("flac-ultrahires")
            .WithName("FLAC Ultra Hi-Res")
            .WithFormat("FLAC")
            .WithSampleRate(192000)
            .WithBitDepth(24)
            .Build();

    /// <summary>Creates a Tidal-style MQA quality.</summary>
    public static StreamingQuality CreateMqa() =>
        new StreamingQualityBuilder()
            .WithId("mqa")
            .WithName("MQA")
            .WithFormat("MQA")
            .WithSampleRate(96000)
            .WithBitDepth(24)
            .Build();
}

/// <summary>
/// Fluent builder for creating <see cref="StreamingSearchResult"/> instances in tests.
/// </summary>
public sealed class StreamingSearchResultBuilder
{
    private string _id = Guid.NewGuid().ToString();
    private string _title = "Search Result";
    private string _artist = "Test Artist";
    private string _album = string.Empty;
    private StreamingSearchType _type = StreamingSearchType.Album;
    private DateTime? _releaseDate;
    private string _genre = string.Empty;
    private string _label = string.Empty;
    private string _coverArtUrl = string.Empty;
    private int? _trackCount;
    private TimeSpan? _duration;
    private readonly Dictionary<string, object> _metadata = new();

    public StreamingSearchResultBuilder WithId(string id)
    {
        _id = id;
        return this;
    }

    public StreamingSearchResultBuilder WithTitle(string title)
    {
        _title = title;
        return this;
    }

    public StreamingSearchResultBuilder WithArtist(string artist)
    {
        _artist = artist;
        return this;
    }

    public StreamingSearchResultBuilder WithAlbum(string album)
    {
        _album = album;
        return this;
    }

    public StreamingSearchResultBuilder WithType(StreamingSearchType type)
    {
        _type = type;
        return this;
    }

    public StreamingSearchResultBuilder WithReleaseDate(DateTime? date)
    {
        _releaseDate = date;
        return this;
    }

    public StreamingSearchResultBuilder WithGenre(string genre)
    {
        _genre = genre;
        return this;
    }

    public StreamingSearchResultBuilder WithLabel(string label)
    {
        _label = label;
        return this;
    }

    public StreamingSearchResultBuilder WithCoverArtUrl(string url)
    {
        _coverArtUrl = url;
        return this;
    }

    public StreamingSearchResultBuilder WithTrackCount(int? count)
    {
        _trackCount = count;
        return this;
    }

    public StreamingSearchResultBuilder WithDuration(TimeSpan? duration)
    {
        _duration = duration;
        return this;
    }

    public StreamingSearchResultBuilder WithMetadata(string key, object value)
    {
        _metadata[key] = value;
        return this;
    }

    public StreamingSearchResult Build() => new()
    {
        Id = _id,
        Title = _title,
        Artist = _artist,
        Album = _album,
        Type = _type,
        ReleaseDate = _releaseDate,
        Genre = _genre,
        Label = _label,
        CoverArtUrl = _coverArtUrl,
        TrackCount = _trackCount,
        Duration = _duration,
        Metadata = new Dictionary<string, object>(_metadata)
    };

    /// <summary>Creates an album search result.</summary>
    public static StreamingSearchResult CreateAlbumResult(string title, string artist) =>
        new StreamingSearchResultBuilder()
            .WithTitle(title)
            .WithArtist(artist)
            .WithType(StreamingSearchType.Album)
            .WithTrackCount(10)
            .Build();

    /// <summary>Creates an artist search result.</summary>
    public static StreamingSearchResult CreateArtistResult(string name) =>
        new StreamingSearchResultBuilder()
            .WithTitle(name)
            .WithArtist(name)
            .WithType(StreamingSearchType.Artist)
            .Build();

    /// <summary>Creates a track search result.</summary>
    public static StreamingSearchResult CreateTrackResult(string title, string artist, string album) =>
        new StreamingSearchResultBuilder()
            .WithTitle(title)
            .WithArtist(artist)
            .WithAlbum(album)
            .WithType(StreamingSearchType.Track)
            .WithDuration(TimeSpan.FromMinutes(3.5))
            .Build();
}
