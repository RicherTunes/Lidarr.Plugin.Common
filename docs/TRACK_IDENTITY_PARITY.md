# Track Identity Mapping Parity

This document defines the **required fields** that all streaming plugins must populate on `StreamingTrack` for consistent metadata application and cross-plugin parity.

## Purpose

Track identity mapping ensures that:
1. Downloaded audio files have consistent, complete metadata tags
2. Users can switch between plugins (e.g., Qobuzarr to Tidalarr) without losing metadata fidelity
3. MusicBrainz integration works across all plugins
4. E2E metadata validation gates can assert consistent behavior

## Required Fields (Tier 1)

These fields **MUST** be populated for basic functionality:

| Field | Type | Example | Notes |
|-------|------|---------|-------|
| `Id` | string | `"12345"` | Service-specific track ID |
| `Title` | string | `"So What"` | Track title (may include version suffix) |
| `Artist.Name` | string | `"Miles Davis"` | Primary performing artist |
| `Album.Title` | string | `"Kind of Blue"` | Album name |
| `Album.Artist.Name` | string | `"Miles Davis"` | Album artist (may differ from track artist) |
| `TrackNumber` | int | `1` | Position on disc (1-based) |
| `Duration` | TimeSpan | `00:09:22` | Track duration |

## Expected Fields (Tier 2)

These fields **SHOULD** be populated when the source API provides them:

| Field | Type | Example | Notes |
|-------|------|---------|-------|
| `DiscNumber` | int | `1` | Disc number for multi-disc albums. Default to 1 if unknown. |
| `Album.ReleaseDate` | DateTime? | `1959-08-17` | Album release date (for Year tag) |
| `Isrc` | string | `"USSM19922509"` | International Standard Recording Code |
| `Album.Genres` | List<string> | `["Jazz"]` | Genre(s) for the album |
| `IsExplicit` | bool | `false` | Explicit content flag |

## Aspirational Fields (Tier 3)

These fields are valuable for advanced integrations but may not be available from all APIs:

| Field | Type | Example | Notes |
|-------|------|---------|-------|
| `MusicBrainzId` | string | `"guid..."` | MusicBrainz Track ID |
| `Album.MusicBrainzId` | string | `"guid..."` | MusicBrainz Release ID |
| `Artist.MusicBrainzId` | string | `"guid..."` | MusicBrainz Artist ID |
| `FeaturedArtists` | List | `[...]` | Featured/guest artists |
| `Composers` | List | `["Miles Davis"]` | Songwriters/composers |

## Current Plugin Status

### Qobuzarr

| Field | Status | Notes |
|-------|--------|-------|
| Tier 1 (all) | :white_check_mark: Populated | Full support |
| DiscNumber | :white_check_mark: Populated | From `media_number` |
| ReleaseDate | :white_check_mark: Populated | From album endpoint |
| Isrc | :white_check_mark: Populated | Direct from Qobuz API |
| MusicBrainzId | :white_check_mark: Populated | Via TrackDownload model |

### Tidalarr

| Field | Status | Notes |
|-------|--------|-------|
| Tier 1 (all) | :white_check_mark: Populated | Full support |
| DiscNumber | :x: Hardcoded=1 | Tidal API limitation |
| ReleaseDate | :warning: Metadata only | Not in Album.ReleaseDate |
| Isrc | :x: Empty | Not fetched from API |
| MusicBrainzId | :x: Empty | Not available from Tidal |

## Metadata Applier Coverage

The `TagLibAudioMetadataApplier` in Common writes these fields:

| StreamingTrack Field | Audio Tag | Written |
|---------------------|-----------|---------|
| Title | ID3: TIT2 / Vorbis: TITLE | :white_check_mark: Yes |
| Artist.Name | ID3: TPE1 / Vorbis: ARTIST | :white_check_mark: Yes |
| Album.Artist.Name | ID3: TPE2 / Vorbis: ALBUMARTIST | :white_check_mark: Yes |
| Album.Title | ID3: TALB / Vorbis: ALBUM | :white_check_mark: Yes |
| TrackNumber | ID3: TRCK / Vorbis: TRACKNUMBER | :white_check_mark: Yes |
| DiscNumber | ID3: TPOS / Vorbis: DISCNUMBER | :white_check_mark: Yes |
| Album.ReleaseDate.Year | ID3: TDRC / Vorbis: DATE | :white_check_mark: Yes |
| Album.Genres[0] | ID3: TCON / Vorbis: GENRE | :white_check_mark: Yes |
| **Isrc** | ID3: TSRC / Vorbis: ISRC | :x: **Not written** |
| **MusicBrainzId** | ID3: TXXX:MUSICBRAINZ_TRACKID | :x: **Not written** |

## Characterization Test Requirements

Each plugin repository should include characterization tests that verify:

1. **Mapper tests**: Assert that `StreamingTrack` fields are populated correctly from API responses
2. **Tag validation**: Assert that written audio files contain expected tags
3. **Round-trip**: Download a known track and verify all Tier 1 + Tier 2 fields are present in tags

Example test structure:
```csharp
[Fact]
public void StreamingTrack_FromApiResponse_HasRequiredFields()
{
    var track = mapper.MapTrack(sampleApiResponse);

    // Tier 1
    Assert.NotEmpty(track.Id);
    Assert.NotEmpty(track.Title);
    Assert.NotEmpty(track.Artist.Name);
    Assert.NotEmpty(track.Album.Title);
    Assert.NotEmpty(track.Album.Artist.Name);
    Assert.True(track.TrackNumber > 0);
    Assert.True(track.Duration > TimeSpan.Zero);

    // Tier 2 (when API provides)
    Assert.True(track.DiscNumber >= 1);
    // Isrc may be empty if API doesn't provide
}
```

## Parity Gaps to Address

1. **TagLib applier**: Extend to write ISRC and MusicBrainz IDs when present
2. **Tidalarr**: Investigate Tidal API for DiscNumber and ISRC availability
3. **E2E gates**: Add metadata tag validation for Tier 1 + 2 fields

## Related Documents

- `docs/ECOSYSTEM_PARITY_ROADMAP.md` - Overall parity goals
- `docs/E2E_ERROR_CODES.md` - E2E_METADATA_MISSING error details
