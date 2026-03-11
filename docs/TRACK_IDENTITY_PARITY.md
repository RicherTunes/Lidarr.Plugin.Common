# Track Identity Mapping Parity

<!-- DOC_VERSION: 2026-01-10-v2 -->
<!-- Plugin tests should reference this version to detect drift -->

This document defines field mapping expectations for `StreamingTrack` across streaming plugins.

## Tier Semantics (READ FIRST)

| Tier | Contract Type | Test Behavior | Meaning |
|------|---------------|---------------|---------|
| **Tier 1** | Hard contract | Tests MUST fail if missing | Core functionality; all plugins MUST implement |
| **Tier 2** | Informational | Tests MUST NOT fail; log warnings | Expected when API provides; document gaps |
| **Tier 3** | Aspirational | Tests MUST NOT fail; skip silently | Nice-to-have; blocked by API or not yet implemented |

**Key rule**: Tier 2/3 characterization tests document current behavior but MUST NOT cause CI failures.
They exist to track parity gaps, not enforce them.

## Purpose

Track identity mapping ensures:
1. Downloaded audio files have consistent, complete metadata tags
2. Users can switch between plugins without losing metadata fidelity
3. MusicBrainz integration works across all plugins
4. E2E metadata validation gates assert consistent behavior

## Tier 1: Required Fields (Hard Contract)

These fields **MUST** be populated. Tests **MUST** fail if missing.

| Field | Type | Example | Notes |
|-------|------|---------|-------|
| `Id` | string | `"12345"` | Service-specific track ID |
| `Title` | string | `"So What"` | Track title (may include version suffix) |
| `Artist.Name` | string | `"Miles Davis"` | Primary performing artist |
| `Album.Title` | string | `"Kind of Blue"` | Album name |
| `Album.Artist.Name` | string | `"Miles Davis"` | Album artist (may differ from track artist) |
| `TrackNumber` | int | `1` | Position on disc (1-based) |
| `Duration` | TimeSpan | `00:09:22` | Track duration |

## Tier 2: Expected Fields (Informational)

These fields **SHOULD** be populated when the source API provides them.
Tests document gaps but **MUST NOT** fail.

| Field | Type | Qobuzarr | Tidalarr | Blocker |
|-------|------|----------|----------|---------|
| `Album.ReleaseDate` | DateTime? | Populated | Metadata dict only | Implementation gap |
| `Isrc` | string | Populated | Empty | API doesn't expose at track level |
| `Album.Genres` | List | Populated | Empty | Implementation gap |
| `IsExplicit` | bool | Populated | Hardcoded false | Implementation gap |

## Tier 3: Aspirational Fields (Informational)

These fields are valuable but blocked by API limitations or not yet implemented.
Tests document status but **MUST NOT** fail.

| Field | Type | Qobuzarr | Tidalarr | Blocker |
|-------|------|----------|----------|---------|
| `DiscNumber` | int | Populated | Hardcoded=1 | **Tidal API limitation** |
| `MusicBrainzId` | string | Via TrackDownload | Empty | Tidal API doesn't provide |
| `Album.MusicBrainzId` | string | Via TrackDownload | Empty | Tidal API doesn't provide |
| `FeaturedArtists` | List | Partial | Empty | Implementation gap |
| `Composers` | List | Populated | Empty | Implementation gap |

**Note on DiscNumber**: Tidal's track API endpoint does not return disc/volume information.
This is a hard API limitation, not an implementation gap. Tidalarr correctly defaults to 1.

## Metadata Applier Coverage

The `TagLibAudioMetadataApplier` in Common writes these fields:

| StreamingTrack Field | Audio Tag | Written | Gap |
|---------------------|-----------|---------|-----|
| Title | ID3: TIT2 / Vorbis: TITLE | Yes | |
| Artist.Name | ID3: TPE1 / Vorbis: ARTIST | Yes | |
| Album.Artist.Name | ID3: TPE2 / Vorbis: ALBUMARTIST | Yes | |
| Album.Title | ID3: TALB / Vorbis: ALBUM | Yes | |
| TrackNumber | ID3: TRCK / Vorbis: TRACKNUMBER | Yes | |
| DiscNumber | ID3: TPOS / Vorbis: DISCNUMBER | Yes | |
| Album.ReleaseDate.Year | ID3: TDRC / Vorbis: DATE | Yes | |
| Album.Genres[0] | ID3: TCON / Vorbis: GENRE | Yes | |
| **Isrc** | ID3: TSRC / Vorbis: ISRC | **No** | Implementation gap |
| **MusicBrainzId** | ID3: TXXX:MUSICBRAINZ_TRACKID | **No** | Implementation gap |

## Characterization Test Requirements

### Tier 1 Tests (MUST fail on missing)
```csharp
[Fact]
public void StreamingTrack_Tier1_AllFieldsPopulated()
{
    var track = mapper.MapTrack(sampleApiResponse);

    Assert.NotEmpty(track.Id);
    Assert.NotEmpty(track.Title);
    Assert.NotEmpty(track.Artist.Name);
    Assert.NotEmpty(track.Album.Title);
    Assert.NotEmpty(track.Album.Artist.Name);
    Assert.True(track.TrackNumber > 0);
    Assert.True(track.Duration > TimeSpan.Zero);
}
```

### Tier 2/3 Tests (MUST NOT fail; document only)
```csharp
/// <summary>
/// Documents current ISRC status. See TRACK_IDENTITY_PARITY.md DOC_VERSION: 2026-01-10-v2
/// </summary>
[Fact]
public void StreamingTrack_Isrc_DocumentCurrentBehavior()
{
    var track = mapper.MapTrack(sampleApiResponse);

    // Document current behavior - DO NOT Assert.NotEmpty
    // Qobuzarr: populated from API
    // Tidalarr: empty (API limitation)
    _output.WriteLine($"Isrc: '{track.Isrc}' (empty={string.IsNullOrEmpty(track.Isrc)})");
}
```

### Doc Reference in Plugin Tests
```csharp
// At top of parity test file:
// Reference: TRACK_IDENTITY_PARITY.md DOC_VERSION: 2026-01-10-v2
// If this version changes, review tests for updated expectations.
```

## Parity Gaps to Address

1. **TagLib applier**: Extend to write ISRC and MusicBrainz IDs when present
2. **ReleaseDate normalization**: Tidalarr should populate `Album.ReleaseDate` not Metadata dict
3. **E2E gates**: Validate Tier 1 fields in metadata; warn on Tier 2/3 gaps

## Related Documents

- `docs/ECOSYSTEM_PARITY_ROADMAP.md` - Overall parity goals
- `docs/E2E_ERROR_CODES.md` - E2E_METADATA_MISSING error details
