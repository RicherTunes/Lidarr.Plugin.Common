using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidarr.Plugin.Common.Services.Intelligence
{
    /// <summary>
    /// Live album title normalization service that handles venue information and date variations.
    /// Sibling to <see cref="CompilationAlbumDetector"/>; targets cross-streaming-service matching
    /// failures for live albums (inconsistent venue formatting, date variations, regional names).
    /// </summary>
    /// <remarks>
    /// Cross-plugin issue: Live albums often fail matching due to:
    /// - Inconsistent venue formatting: "Live at Madison Square Garden" vs "Live from MSG"
    /// - Date format variations: "2023-12-31" vs "December 31, 2023" vs "New Year's Eve 2023"
    /// - Regional venue name differences: "Royal Albert Hall" vs "Albert Hall"
    /// - Recording context variations: "Live at", "Recorded at", "From", etc.
    ///
    /// This normalizer:
    /// 1. Identifies live album patterns and extracts core album title
    /// 2. Normalizes venue names to canonical forms
    /// 3. Standardizes date formats for consistent comparison
    /// 4. Creates fuzzy matching patterns for venue variations
    /// 5. Enables optimization for live album catalogs that would otherwise fail
    /// </remarks>
    public class LiveAlbumNormalizer
    {
        private readonly ILogger _logger;

        // Live album context indicators
        private static readonly string[] LiveContextMarkers =
        {
            "live", "concert", "recorded", "performance", "show", "tour",
            "festival", "session", "unplugged", "acoustic", "mtv", "bbc"
        };

        // Common venue prefixes that should be normalized
        private static readonly Dictionary<string, string[]> VenuePatterns = new()
        {
            // Theater and concert halls
            ["theater"] = new[] { "theatre", "theater", "playhouse", "opera house", "concert hall" },
            ["arena"] = new[] { "arena", "stadium", "dome", "center", "centre", "coliseum", "colosseum" },
            ["club"] = new[] { "club", "bar", "pub", "tavern", "lounge", "cafe" },
            ["festival"] = new[] { "festival", "fest", "celebration", "gathering", "jamboree" },

            // Famous venue normalizations
            ["madison square garden"] = new[] { "msg", "madison square garden", "the garden" },
            ["royal albert hall"] = new[] { "albert hall", "royal albert hall", "rah" },
            ["wembley"] = new[] { "wembley stadium", "wembley arena", "wembley" },
            ["red rocks"] = new[] { "red rocks amphitheatre", "red rocks amphitheater", "red rocks" },
            ["hollywood bowl"] = new[] { "hollywood bowl", "the bowl" },
            ["carnegie hall"] = new[] { "carnegie hall", "carnegie" }
        };

        private const RegexOptions DefaultOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

        // Date format patterns for normalization
        private static readonly Regex IsoDateRegex = new(@"\b(\d{4})[\/\-](\d{1,2})[\/\-](\d{1,2})\b", DefaultOptions, RegexTimeout);
        private static readonly Regex UsDateRegex = new(@"\b(\d{1,2})[\/\-](\d{1,2})[\/\-](\d{4})\b", DefaultOptions, RegexTimeout);
        private static readonly Regex MonthNameDateRegex = new(@"\b(january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)\s+(\d{1,2}),?\s+(\d{4})\b", DefaultOptions, RegexTimeout);
        private static readonly Regex YearRangeRegex = new(@"\b(\d{4})[\/\-](\d{2,4})\b", DefaultOptions, RegexTimeout);
        private static readonly Regex SimpleYearRegex = new(@"\b(\d{4})\b", RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
        private static readonly Regex ExtractYearRegex = new(@"\b(19|20)\d{2}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);

        private static readonly Regex[] DatePatterns =
        {
            IsoDateRegex, UsDateRegex, MonthNameDateRegex, YearRangeRegex, SimpleYearRegex
        };

        // Live album title patterns for extraction
        private static readonly Regex LiveAtVenueParenthesesRegex = new(@"^(.+?)\s*[\(\[]\s*(?:live|recorded|concert|performance)\s+(?:at|from|in)\s+([^,\]\)]+)(?:,\s*([^\]\)]+))?\s*[\)\]]$", DefaultOptions, RegexTimeout);
        private static readonly Regex LiveAtVenueDashRegex = new(@"^(.+?)\s*[-–—]\s*(?:live|recorded|concert)\s+(?:at|from|in)\s+(.+)$", DefaultOptions, RegexTimeout);
        private static readonly Regex VenueColonTitleRegex = new(@"^(?:live|recorded|concert)\s+(?:at|from|in)\s+([^:]+):\s*(.+)$", DefaultOptions, RegexTimeout);
        private static readonly Regex TitleSuffixLiveRegex = new(@"^(.+?)\s+(?:live|concert|unplugged|acoustic)$", DefaultOptions, RegexTimeout);
        private static readonly Regex SpecialSessionPrefixRegex = new(@"^(?:mtv\s+unplugged|bbc\s+session|live\s+session):\s*(.+)$", DefaultOptions, RegexTimeout);

        private static readonly Regex[] LiveAlbumPatterns =
        {
            LiveAtVenueParenthesesRegex, LiveAtVenueDashRegex, VenueColonTitleRegex, TitleSuffixLiveRegex, SpecialSessionPrefixRegex
        };

        // Title cleanup patterns
        private static readonly Regex LiveDashSuffixRegex = new(@"\s*[-–—]\s*live.*$", DefaultOptions, RegexTimeout);
        private static readonly Regex LiveParenthesesSuffixRegex = new(@"\s*[\(\[]\s*live.*[\)\]]", DefaultOptions, RegexTimeout);
        private static readonly Regex LiveWordSuffixRegex = new(@"\s+live$", DefaultOptions, RegexTimeout);

        // Venue normalization patterns
        private static readonly Regex LeadingTheRegex = new(@"\b(the\s+)", DefaultOptions, RegexTimeout);
        private static readonly Regex VenueTypeSuffixRegex = new(@"\s+(arena|stadium|theater|theatre|hall|center|centre)$", DefaultOptions, RegexTimeout);

        // Special live album markers that indicate recording context
        private static readonly Dictionary<string, string> SpecialLiveMarkers = new()
        {
            ["mtv unplugged"] = "MTV Unplugged",
            ["bbc session"] = "BBC Session",
            ["bbc live"] = "BBC Session",
            ["live session"] = "Live Session",
            ["acoustic session"] = "Acoustic Session",
            ["radio session"] = "Radio Session",
            ["studio session"] = "Studio Session"
        };

        /// <summary>Creates a new <see cref="LiveAlbumNormalizer"/>.</summary>
        public LiveAlbumNormalizer(ILogger<LiveAlbumNormalizer>? logger = null)
        {
            _logger = (ILogger?)logger ?? NullLogger.Instance;
        }

        /// <summary>
        /// Normalizes live album titles for better matching by extracting core titles and standardizing venue/date info.
        /// </summary>
        /// <param name="albumTitle">Original album title to normalize</param>
        /// <param name="options">Normalization options for different use cases</param>
        /// <returns>Normalized title result with extracted components</returns>
        public LiveAlbumNormalizationResult NormalizeLiveAlbum(string? albumTitle, LiveAlbumNormalizationOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
            {
                return new LiveAlbumNormalizationResult
                {
                    IsLiveAlbum = false,
                    NormalizedTitle = string.Empty,
                    OriginalTitle = albumTitle ?? string.Empty
                };
            }

            options ??= LiveAlbumNormalizationOptions.Default;

            var result = new LiveAlbumNormalizationResult
            {
                OriginalTitle = albumTitle,
                IsLiveAlbum = IsLiveAlbum(albumTitle)
            };

            if (!result.IsLiveAlbum && !options.ForceProcessing)
            {
                result.NormalizedTitle = albumTitle.Trim();
                return result;
            }

            try
            {
                ExtractLiveAlbumComponents(albumTitle, result);

                if (!string.IsNullOrWhiteSpace(result.Venue) && options.NormalizeVenues)
                {
                    result.NormalizedVenue = NormalizeVenueName(result.Venue!);
                }

                if (!string.IsNullOrWhiteSpace(result.Date) && options.NormalizeDates)
                {
                    result.NormalizedDate = NormalizeDateString(result.Date!);
                }

                result.NormalizedTitle = CreateNormalizedTitle(result, options);

                if (options.GenerateVariations)
                {
                    result.TitleVariations = GenerateTitleVariations(result);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to normalize live album: {AlbumTitle}", albumTitle);
                result.NormalizedTitle = albumTitle;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        /// <summary>Determines if an album title indicates it's a live recording.</summary>
        public bool IsLiveAlbum(string? albumTitle)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
                return false;

            var normalizedTitle = albumTitle.ToLowerInvariant();

            // Check for explicit live markers as whole-word matches (avoid false positives like "Live wire").
            // Use word-boundary regex against original text for the markers so "live" doesn't match
            // inside other words.
            var hasLiveMarker = false;
            foreach (var marker in LiveContextMarkers)
            {
                if (Regex.IsMatch(normalizedTitle, $@"\b{Regex.Escape(marker)}\b", RegexOptions.None, RegexTimeout))
                {
                    hasLiveMarker = true;
                    break;
                }
            }

            if (!hasLiveMarker)
            {
                var hasSpecialMarker = SpecialLiveMarkers.Keys.Any(marker => normalizedTitle.Contains(marker));
                if (hasSpecialMarker) return true;

                var hasLivePattern = LiveAlbumPatterns.Any(pattern => pattern.IsMatch(albumTitle));
                return hasLivePattern;
            }

            return true;
        }

        /// <summary>
        /// Calculates similarity between two live album titles with venue/date awareness.
        /// </summary>
        /// <returns>Similarity score from 0.0 to 1.0</returns>
        public double CalculateLiveAlbumSimilarity(
            string? title1,
            string? title2,
            LiveAlbumNormalizationOptions? options = null)
        {
            if (string.IsNullOrEmpty(title1) && string.IsNullOrEmpty(title2))
                return 1.0;

            if (string.IsNullOrEmpty(title1) || string.IsNullOrEmpty(title2))
                return 0.0;

            options ??= LiveAlbumNormalizationOptions.Default;

            var normalized1 = NormalizeLiveAlbum(title1, options);
            var normalized2 = NormalizeLiveAlbum(title2, options);

            var coreSimilarity = CalculateStringSimilarity(
                normalized1.CoreAlbumTitle ?? normalized1.NormalizedTitle ?? string.Empty,
                normalized2.CoreAlbumTitle ?? normalized2.NormalizedTitle ?? string.Empty);

            if (normalized1.IsLiveAlbum && normalized2.IsLiveAlbum)
            {
                var venueSimilarity = CalculateVenueSimilarity(normalized1.NormalizedVenue, normalized2.NormalizedVenue);
                var dateSimilarity = CalculateDateSimilarity(normalized1.NormalizedDate, normalized2.NormalizedDate);

                // Weight: 70% core title, 20% venue, 10% date
                return (coreSimilarity * 0.7) + (venueSimilarity * 0.2) + (dateSimilarity * 0.1);
            }

            return coreSimilarity;
        }

        // ----- Private helpers -----

        private void ExtractLiveAlbumComponents(string albumTitle, LiveAlbumNormalizationResult result)
        {
            foreach (var pattern in LiveAlbumPatterns)
            {
                var match = pattern.Match(albumTitle);
                if (match.Success)
                {
                    switch (match.Groups.Count)
                    {
                        case 2:
                            result.CoreAlbumTitle = match.Groups[1].Value.Trim();
                            break;

                        case 3:
                            // Distinguish "Live at Venue: Album Title" (pattern source begins with live keyword)
                            if (pattern.ToString().StartsWith("^(?:live", StringComparison.OrdinalIgnoreCase))
                            {
                                result.Venue = match.Groups[1].Value.Trim();
                                result.CoreAlbumTitle = match.Groups[2].Value.Trim();
                            }
                            else
                            {
                                result.CoreAlbumTitle = match.Groups[1].Value.Trim();
                                result.Venue = match.Groups[2].Value.Trim();
                            }
                            break;

                        case 4:
                            result.CoreAlbumTitle = match.Groups[1].Value.Trim();
                            result.Venue = match.Groups[2].Value.Trim();
                            result.Date = match.Groups[3].Success ? match.Groups[3].Value.Trim() : null;
                            break;
                    }

                    result.PatternMatched = pattern.ToString();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(result.CoreAlbumTitle))
            {
                result.CoreAlbumTitle = ExtractCoreTitle(albumTitle);
            }

            CheckSpecialLiveMarkers(albumTitle, result);
        }

        private static string ExtractCoreTitle(string albumTitle)
        {
            var title = albumTitle;

            title = LiveDashSuffixRegex.Replace(title, "");
            title = LiveParenthesesSuffixRegex.Replace(title, "");
            title = LiveWordSuffixRegex.Replace(title, "");

            foreach (var marker in SpecialLiveMarkers.Keys)
            {
                title = Regex.Replace(title, $@"^{Regex.Escape(marker)}:\s*", "", RegexOptions.IgnoreCase, RegexTimeout);
                title = Regex.Replace(title, $@"\s*[-–—]\s*{Regex.Escape(marker)}.*$", "", RegexOptions.IgnoreCase, RegexTimeout);
            }

            return title.Trim();
        }

        private static void CheckSpecialLiveMarkers(string albumTitle, LiveAlbumNormalizationResult result)
        {
            var normalizedTitle = albumTitle.ToLowerInvariant();

            foreach (var marker in SpecialLiveMarkers)
            {
                if (normalizedTitle.Contains(marker.Key))
                {
                    result.SpecialContext = marker.Value;
                    result.IsSpecialSession = true;
                    break;
                }
            }
        }

        private static string NormalizeVenueName(string venue)
        {
            if (string.IsNullOrWhiteSpace(venue))
                return venue;

            var normalized = venue.ToLowerInvariant().Trim();

            foreach (var pattern in VenuePatterns)
            {
                if (pattern.Value.Any(variation => normalized.Contains(variation)))
                {
                    return pattern.Key;
                }
            }

            normalized = LeadingTheRegex.Replace(normalized, "");
            normalized = VenueTypeSuffixRegex.Replace(normalized, " venue");

            return normalized.Trim();
        }

        private static string NormalizeDateString(string date)
        {
            if (string.IsNullOrWhiteSpace(date))
                return date;

            foreach (var pattern in DatePatterns)
            {
                var match = pattern.Match(date);
                if (match.Success)
                {
                    var groups = match.Groups.Cast<Group>().Skip(1).Where(g => g.Success).ToList();
                    var year = groups.FirstOrDefault(g => g.Value.Length == 4)?.Value;
                    if (!string.IsNullOrEmpty(year) && int.TryParse(year, out var yearInt) && yearInt >= 1950 && yearInt <= DateTime.Now.Year + 1)
                    {
                        return year!;
                    }
                }
            }

            var yearMatch = ExtractYearRegex.Match(date);
            if (yearMatch.Success)
            {
                return yearMatch.Value;
            }

            return date.Trim();
        }

        private static string CreateNormalizedTitle(LiveAlbumNormalizationResult result, LiveAlbumNormalizationOptions options)
        {
            var coreTitle = result.CoreAlbumTitle ?? result.OriginalTitle ?? string.Empty;

            if (!options.IncludeLiveContext || !result.IsLiveAlbum)
            {
                return coreTitle;
            }

            var components = new List<string> { coreTitle };

            if (result.IsSpecialSession && !string.IsNullOrWhiteSpace(result.SpecialContext))
            {
                components.Add($"({result.SpecialContext})");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(result.NormalizedVenue) && options.IncludeVenue)
                {
                    components.Add($"(Live at {result.NormalizedVenue})");
                }
                else if (result.IsLiveAlbum)
                {
                    components.Add("(Live)");
                }
            }

            return string.Join(" ", components);
        }

        private static List<string> GenerateTitleVariations(LiveAlbumNormalizationResult result)
        {
            var variations = new List<string>();

            if (!string.IsNullOrWhiteSpace(result.CoreAlbumTitle))
            {
                variations.Add(result.CoreAlbumTitle!);
            }

            if (!string.IsNullOrWhiteSpace(result.NormalizedTitle))
            {
                variations.Add(result.NormalizedTitle!);
            }

            if (result.IsLiveAlbum && !string.IsNullOrWhiteSpace(result.CoreAlbumTitle))
            {
                variations.Add($"{result.CoreAlbumTitle} (Live)");
                variations.Add($"{result.CoreAlbumTitle} - Live");
                variations.Add($"Live: {result.CoreAlbumTitle}");

                if (!string.IsNullOrWhiteSpace(result.NormalizedVenue))
                {
                    variations.Add($"{result.CoreAlbumTitle} (Live at {result.NormalizedVenue})");
                }
            }

            return variations.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static double CalculateVenueSimilarity(string? venue1, string? venue2)
        {
            if (string.IsNullOrEmpty(venue1) && string.IsNullOrEmpty(venue2))
                return 1.0;

            if (string.IsNullOrEmpty(venue1) || string.IsNullOrEmpty(venue2))
                return 0.5;

            if (venue1!.Equals(venue2, StringComparison.OrdinalIgnoreCase))
                return 1.0;

            return CalculateStringSimilarity(venue1, venue2!);
        }

        private static double CalculateDateSimilarity(string? date1, string? date2)
        {
            if (string.IsNullOrEmpty(date1) && string.IsNullOrEmpty(date2))
                return 1.0;

            if (string.IsNullOrEmpty(date1) || string.IsNullOrEmpty(date2))
                return 0.8;

            if (date1!.Equals(date2, StringComparison.OrdinalIgnoreCase))
                return 1.0;

            if (int.TryParse(date1, out var year1) && int.TryParse(date2, out var year2))
            {
                var yearDiff = Math.Abs(year1 - year2);
                return yearDiff == 0 ? 1.0 : yearDiff <= 1 ? 0.9 : yearDiff <= 2 ? 0.7 : 0.3;
            }

            return CalculateStringSimilarity(date1!, date2!);
        }

        // Internal Levenshtein-based similarity (mirrors UnicodeNormalizer's simple shared impl).
        private static double CalculateStringSimilarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2)) return 1.0;
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2)) return 0.0;
            if (string.Equals(s1, s2, StringComparison.OrdinalIgnoreCase)) return 1.0;

            var maxLen = Math.Max(s1.Length, s2.Length);
            var distance = LevenshteinDistance(s1.ToLowerInvariant(), s2.ToLowerInvariant());
            return 1.0 - (double)distance / maxLen;
        }

        private static int LevenshteinDistance(string s1, string s2)
        {
            var matrix = new int[s1.Length + 1, s2.Length + 1];

            for (int i = 0; i <= s1.Length; i++) matrix[i, 0] = i;
            for (int j = 0; j <= s2.Length; j++) matrix[0, j] = j;

            for (int i = 1; i <= s1.Length; i++)
            {
                for (int j = 1; j <= s2.Length; j++)
                {
                    var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                    matrix[i, j] = Math.Min(
                        Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                        matrix[i - 1, j - 1] + cost);
                }
            }

            return matrix[s1.Length, s2.Length];
        }
    }

    /// <summary>Options controlling <see cref="LiveAlbumNormalizer"/> behavior.</summary>
    public class LiveAlbumNormalizationOptions
    {
        /// <summary>If true, attempts to canonicalize venue names.</summary>
        public bool NormalizeVenues { get; set; } = true;

        /// <summary>If true, attempts to extract a year from raw date strings.</summary>
        public bool NormalizeDates { get; set; } = true;

        /// <summary>If true, includes a (Live) / (Live at X) suffix in the normalized title.</summary>
        public bool IncludeLiveContext { get; set; } = true;

        /// <summary>If true, the (Live at Venue) suffix is preferred over plain (Live).</summary>
        public bool IncludeVenue { get; set; } = false;

        /// <summary>If true, returns a list of fuzzy-matching title variations.</summary>
        public bool GenerateVariations { get; set; } = true;

        /// <summary>If true, runs normalization even when the title is not detected as live.</summary>
        public bool ForceProcessing { get; set; } = false;

        /// <summary>Default options (minimal context, generate variations).</summary>
        public static LiveAlbumNormalizationOptions Default => new();

        /// <summary>Strip live context entirely — returns only the core title.</summary>
        public static LiveAlbumNormalizationOptions CoreTitleOnly => new()
        {
            NormalizeVenues = true,
            NormalizeDates = true,
            IncludeLiveContext = false,
            IncludeVenue = false,
            GenerateVariations = false,
            ForceProcessing = false
        };

        /// <summary>Full normalization including venue and variations.</summary>
        public static LiveAlbumNormalizationOptions FullContext => new()
        {
            NormalizeVenues = true,
            NormalizeDates = true,
            IncludeLiveContext = true,
            IncludeVenue = true,
            GenerateVariations = true,
            ForceProcessing = true
        };
    }

    /// <summary>Result of <see cref="LiveAlbumNormalizer.NormalizeLiveAlbum"/>.</summary>
    public class LiveAlbumNormalizationResult
    {
        /// <summary>The original input title.</summary>
        public string? OriginalTitle { get; set; }

        /// <summary>Normalized title (with optional (Live) / (Live at Venue) suffix).</summary>
        public string? NormalizedTitle { get; set; }

        /// <summary>Just the core album title with all live context stripped.</summary>
        public string? CoreAlbumTitle { get; set; }

        /// <summary>Raw venue text extracted from title.</summary>
        public string? Venue { get; set; }

        /// <summary>Canonicalized venue name (e.g. "msg" → "madison square garden").</summary>
        public string? NormalizedVenue { get; set; }

        /// <summary>Raw date string extracted from title.</summary>
        public string? Date { get; set; }

        /// <summary>Year extracted from the raw date if recognizable.</summary>
        public string? NormalizedDate { get; set; }

        /// <summary>True if the title was detected as a live recording.</summary>
        public bool IsLiveAlbum { get; set; }

        /// <summary>True if a special-session marker (MTV Unplugged etc.) matched.</summary>
        public bool IsSpecialSession { get; set; }

        /// <summary>Friendly label for the special context (e.g. "MTV Unplugged").</summary>
        public string? SpecialContext { get; set; }

        /// <summary>Pattern source (regex) that matched, if any.</summary>
        public string? PatternMatched { get; set; }

        /// <summary>Generated title variations for fuzzy matching.</summary>
        public List<string> TitleVariations { get; set; } = new();

        /// <summary>Error message if normalization failed.</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>Returns the best title for matching (core title preferred).</summary>
        public string GetBestMatchingTitle()
            => !string.IsNullOrWhiteSpace(CoreAlbumTitle) ? CoreAlbumTitle! : (NormalizedTitle ?? string.Empty);

        /// <summary>True if any variation contains, or is contained by, the other title.</summary>
        public bool CanMatchAgainst(string? otherTitle)
        {
            if (string.IsNullOrWhiteSpace(otherTitle))
                return false;

            var normalizedOther = otherTitle.ToLowerInvariant();

            return TitleVariations.Any(variation =>
                normalizedOther.Contains(variation.ToLowerInvariant()) ||
                variation.ToLowerInvariant().Contains(normalizedOther));
        }
    }
}
