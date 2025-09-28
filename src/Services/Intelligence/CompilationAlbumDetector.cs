using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Lidarr.Plugin.Common.Services.Intelligence
{
    /// <summary>
    /// Specialized detector for compilation albums and various artists scenarios
    /// Prevents systematic optimization failures for compilations, soundtracks, and mixed-artist albums
    /// UNIVERSAL: All streaming services struggle with Various Artists matching
    /// </summary>
    /// <remarks>
    /// Critical Issue: Various Artists albums often fail optimization because:
    /// - Lidarr: Album Artist = "Various Artists", individual track artists
    /// - Streaming Services: Album Artist = "First Artist" or different attribution pattern
    /// - Standard matching fails due to artist mismatch
    /// 
    /// This detector:
    /// 1. Identifies compilation patterns in both platforms
    /// 2. Applies specialized matching logic for various artists scenarios  
    /// 3. Enables optimization for entire category of albums that would otherwise fail
    /// 4. Handles soundtracks, tribute albums, genre compilations, DJ mixes
    /// </remarks>
    public class CompilationAlbumDetector
    {
        // Various Artists identification patterns
        private static readonly string[] VariousArtistsPatterns =
        {
            "various artists", "v.a.", "va", "various", "compilation", "mixed",
            "sampler", "collection", "anthology", "tribute", "best of various"
        };

        // Soundtrack identification patterns
        private static readonly string[] SoundtrackPatterns =
        {
            "soundtrack", "ost", "original soundtrack", "motion picture soundtrack",
            "movie soundtrack", "film soundtrack", "tv soundtrack", "television soundtrack",
            "game soundtrack", "video game soundtrack", "original motion picture",
            "score", "theme", "themes"
        };

        // Compilation album title patterns
        private static readonly Regex[] CompilationTitleRegexes =
        {
            new(@"\b(?:greatest\s+hits?|best\s+of|the\s+very\s+best|ultimate\s+collection)\b", RegexOptions.IgnoreCase),
            new(@"\b(?:essential|definitive|complete|anthology|retrospective)\b", RegexOptions.IgnoreCase),
            new(@"\b(?:hits|singles|rarities|b-sides|unreleased)\s+(?:collection|compilation|vol)\b", RegexOptions.IgnoreCase),
            new(@"\bvol\.?\s*\d+\b", RegexOptions.IgnoreCase),
            new(@"\b(?:disc|cd)\s*\d+\b", RegexOptions.IgnoreCase)
        };

        /// <summary>
        /// Determines if album is likely a various artists compilation
        /// </summary>
        public static bool IsVariousArtists(string albumArtist, string albumTitle = null)
        {
            if (string.IsNullOrWhiteSpace(albumArtist))
                return false;

            var artistLower = albumArtist.ToLowerInvariant();

            // Direct various artists pattern match
            if (VariousArtistsPatterns.Any(pattern => artistLower.Contains(pattern)))
                return true;

            // Check album title for compilation indicators
            if (!string.IsNullOrWhiteSpace(albumTitle))
            {
                var titleLower = albumTitle.ToLowerInvariant();

                // Soundtrack detection
                if (SoundtrackPatterns.Any(pattern => titleLower.Contains(pattern)))
                    return true;

                // Compilation title patterns
                if (CompilationTitleRegexes.Any(regex => regex.IsMatch(albumTitle)))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Determines if album is a soundtrack
        /// </summary>
        public static bool IsSoundtrack(string albumTitle, string albumArtist = null)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
                return false;

            var titleLower = albumTitle.ToLowerInvariant();
            var artistLower = albumArtist?.ToLowerInvariant() ?? "";

            return SoundtrackPatterns.Any(pattern =>
                titleLower.Contains(pattern) || artistLower.Contains(pattern));
        }

        /// <summary>
        /// Gets compilation type for specialized handling
        /// </summary>
        public static CompilationType GetCompilationType(string albumArtist, string albumTitle = null)
        {
            if (IsSoundtrack(albumTitle, albumArtist))
                return CompilationType.Soundtrack;

            if (IsVariousArtists(albumArtist, albumTitle))
            {
                if (!string.IsNullOrWhiteSpace(albumTitle))
                {
                    var titleLower = albumTitle.ToLowerInvariant();

                    if (titleLower.Contains("greatest hits") || titleLower.Contains("best of"))
                        return CompilationType.GreatestHits;

                    if (titleLower.Contains("live") || titleLower.Contains("concert"))
                        return CompilationType.LiveCompilation;

                    if (titleLower.Contains("tribute"))
                        return CompilationType.Tribute;
                }

                return CompilationType.VariousArtists;
            }

            return CompilationType.Standard;
        }

        /// <summary>
        /// Provides matching strategy recommendations for compilation types
        /// </summary>
        public static CompilationMatchingStrategy GetMatchingStrategy(CompilationType type)
        {
            return type switch
            {
                CompilationType.VariousArtists => CompilationMatchingStrategy.TitleAndYearOnly,
                CompilationType.Soundtrack => CompilationMatchingStrategy.TitleFuzzyMatch,
                CompilationType.GreatestHits => CompilationMatchingStrategy.ArtistAndTitleFuzzy,
                CompilationType.LiveCompilation => CompilationMatchingStrategy.TitleAndVenueMatch,
                CompilationType.Tribute => CompilationMatchingStrategy.TitleAndGenreMatch,
                _ => CompilationMatchingStrategy.Standard
            };
        }
    }

    /// <summary>
    /// Types of compilation albums requiring different matching strategies
    /// </summary>
    public enum CompilationType
    {
        Standard,
        VariousArtists,
        Soundtrack,
        GreatestHits,
        LiveCompilation,
        Tribute
    }

    /// <summary>
    /// Matching strategies for different compilation types
    /// </summary>
    public enum CompilationMatchingStrategy
    {
        Standard,
        TitleAndYearOnly,
        TitleFuzzyMatch,
        ArtistAndTitleFuzzy,
        TitleAndVenueMatch,
        TitleAndGenreMatch
    }
}