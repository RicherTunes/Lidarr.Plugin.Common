using Lidarr.Plugin.Common.Services.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Globalization
{
    /// <summary>
    /// Wave-2 coverage: <see cref="UnicodeNormalizer"/> (~631 lines: CJK romanization
    /// tables, diacritic folding, script detection, edit-distance similarity) had zero
    /// test references prior to this file, despite backing qobuz's live similarity
    /// scoring for artist/album matching. This does not attempt to cover all 631 lines --
    /// it hits diacritic folding, a sampling of the CJK romanization tables, width/case
    /// normalization, known-variation matching, script detection, and idempotence.
    /// </summary>
    public class UnicodeNormalizerTests
    {
        private static UnicodeNormalizer CreateNormalizer()
            => new(NullLogger<UnicodeNormalizer>.Instance);

        // ---------------------------------------------------------------
        // Diacritic folding
        // ---------------------------------------------------------------

        [Theory]
        [InlineData("café", "cafe")]
        [InlineData("Björk", "bjork")]
        [InlineData("Mylène Farmer", "mylene farmer")]
        [InlineData("naïve", "naive")]
        [InlineData("Zürich", "zurich")]
        public void NormalizeForMatching_FoldsDiacritics(string input, string expected)
        {
            var normalizer = CreateNormalizer();
            var result = normalizer.NormalizeForMatching(input);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void NormalizeForMatching_WithDiacriticsDisabled_PreservesAccents()
        {
            var normalizer = CreateNormalizer();
            var options = UnicodeNormalizationOptions.Conservative; // RemoveDiacritics = false
            var result = normalizer.NormalizeForMatching("café", options);
            Assert.Contains("é", result);
        }

        // ---------------------------------------------------------------
        // Case / whitespace / punctuation normalization
        // ---------------------------------------------------------------

        [Fact]
        public void NormalizeForMatching_LowercasesText()
        {
            var normalizer = CreateNormalizer();
            Assert.Equal("hello world", normalizer.NormalizeForMatching("HELLO WORLD"));
        }

        [Fact]
        public void NormalizeForMatching_CollapsesRepeatedWhitespace()
        {
            var normalizer = CreateNormalizer();
            var result = normalizer.NormalizeForMatching("Hello    World  ");
            Assert.Equal("hello world", result);
        }

        [Fact]
        public void NormalizeForMatching_ReplacesSmartQuotesAndDashes()
        {
            var normalizer = CreateNormalizer();
            // ‘ ’ smart apostrophes, — em-dash, … ellipsis
            var input = "Rock ‘n’ Roll — Live…";
            var result = normalizer.NormalizeForMatching(input);
            // Punctuation normalization step then strips non-word chars entirely, so the
            // net effect is the punctuation-derived characters are gone, not literally kept.
            Assert.DoesNotContain("‘", result);
            Assert.DoesNotContain("’", result);
            Assert.DoesNotContain("—", result);
            Assert.DoesNotContain("…", result);
        }

        [Fact]
        public void NormalizeForMatching_NullOrWhitespaceInput_ReturnsEmptyString()
        {
            var normalizer = CreateNormalizer();
            Assert.Equal(string.Empty, normalizer.NormalizeForMatching(null!));
            Assert.Equal(string.Empty, normalizer.NormalizeForMatching("   "));
            Assert.Equal(string.Empty, normalizer.NormalizeForMatching(string.Empty));
        }

        // ---------------------------------------------------------------
        // CJK romanization table sampling
        // ---------------------------------------------------------------

        [Fact]
        public void NormalizeForMatching_HangulSyllables_AreScriptDetectedAsHangul()
        {
            var normalizer = CreateNormalizer();
            var detection = normalizer.DetectScript("블랙핑크");
            Assert.Equal("Hangul", detection.PrimaryScript);
            Assert.True(detection.IsCJK);
        }

        [Fact]
        public void NormalizeForMatching_HiraganaText_AppliesJapaneseRomanizationTable()
        {
            var normalizer = CreateNormalizer();
            // "あか" = hiragana "a" + "ka" -- both present verbatim in the Japanese
            // romanization map -- so with HandleRomanization enabled this should be
            // transliterated toward "aka" rather than left as raw hiragana.
            var result = normalizer.NormalizeForMatching("あか");
            Assert.DoesNotContain("あ", result);
            Assert.DoesNotContain("か", result);
            Assert.Contains("a", result);
        }

        [Fact]
        public void DetectScript_CjkIdeographs_DetectedAsCjk()
        {
            var normalizer = CreateNormalizer();
            var detection = normalizer.DetectScript("周杰倫"); // Jay Chou, Chinese characters
            Assert.Equal("CJK", detection.PrimaryScript);
            Assert.True(detection.IsCJK);
        }

        [Fact]
        public void DetectScript_KatakanaText_DetectedAsKatakana()
        {
            var normalizer = CreateNormalizer();
            var detection = normalizer.DetectScript("ベビーメタル"); // BABYMETAL in katakana
            Assert.Equal("Katakana", detection.PrimaryScript);
        }

        [Fact]
        public void DetectScript_ArabicText_DetectedAsRightToLeft()
        {
            var normalizer = CreateNormalizer();
            var detection = normalizer.DetectScript("مرحبا");
            Assert.Equal("Arabic", detection.PrimaryScript);
            Assert.True(detection.IsRightToLeft);
        }

        [Fact]
        public void DetectScript_LatinAsciiText_DetectedAsLatin()
        {
            var normalizer = CreateNormalizer();
            var detection = normalizer.DetectScript("Rammstein");
            Assert.Equal("Latin", detection.PrimaryScript);
            Assert.False(detection.IsRightToLeft);
            Assert.False(detection.IsCJK);
        }

        // ---------------------------------------------------------------
        // Known-variation matching (used by CalculateInternationalSimilarity)
        // ---------------------------------------------------------------

        [Theory]
        [InlineData("블랙핑크", "BLACKPINK")]
        [InlineData("BlackPink", "Black Pink")]
        [InlineData("방탄소년단", "BTS")]
        public void CalculateInternationalSimilarity_KnownArtistVariations_ScoreAsFullMatch(string a, string b)
        {
            var normalizer = CreateNormalizer();
            var score = normalizer.CalculateInternationalSimilarity(a, b);
            Assert.Equal(1.0, score);
        }

        [Fact]
        public void CalculateInternationalSimilarity_UnrelatedStrings_ScoresLow()
        {
            var normalizer = CreateNormalizer();
            var score = normalizer.CalculateInternationalSimilarity("Rammstein", "Daft Punk");
            Assert.True(score < 0.5, $"expected low similarity, got {score}");
        }

        [Fact]
        public void CalculateInternationalSimilarity_IdenticalStrings_ScoresPerfect()
        {
            var normalizer = CreateNormalizer();
            Assert.Equal(1.0, normalizer.CalculateInternationalSimilarity("Björk", "Björk"));
        }

        [Fact]
        public void CalculateInternationalSimilarity_BothEmpty_ScoresPerfect()
        {
            var normalizer = CreateNormalizer();
            Assert.Equal(1.0, normalizer.CalculateInternationalSimilarity("", ""));
        }

        [Fact]
        public void CalculateInternationalSimilarity_OneEmpty_ScoresZero()
        {
            var normalizer = CreateNormalizer();
            Assert.Equal(0.0, normalizer.CalculateInternationalSimilarity("", "Björk"));
        }

        [Fact]
        public void CalculateInternationalSimilarity_SubstringMatch_ScoresByLengthRatio()
        {
            var normalizer = CreateNormalizer();
            // "bjork" is a substring of "bjork live" after normalization.
            var score = normalizer.CalculateInternationalSimilarity("Björk", "Björk Live");
            Assert.InRange(score, 0.3, 0.99);
        }

        // ---------------------------------------------------------------
        // Idempotence: normalize(normalize(x)) == normalize(x)
        // ---------------------------------------------------------------

        [Theory]
        [InlineData("café")]
        [InlineData("Björk")]
        [InlineData("블랙핑크")]
        [InlineData("周杰倫")]
        [InlineData("Rock 'n' Roll — Live...")]
        [InlineData("HELLO    world")]
        [InlineData("BABYMETAL ベビーメタル")]
        [InlineData("")]
        public void NormalizeForMatching_IsIdempotent(string input)
        {
            var normalizer = CreateNormalizer();
            var once = normalizer.NormalizeForMatching(input);
            var twice = normalizer.NormalizeForMatching(once);
            Assert.Equal(once, twice);
        }

        [Fact]
        public void NormalizeForMatching_IsIdempotent_WithDefaultOptionsAcrossMultipleCalls()
        {
            var normalizer = CreateNormalizer();
            var input = "Mylène Farmer — Live à Bercy";
            var normalized = normalizer.NormalizeForMatching(input);
            for (var i = 0; i < 3; i++)
            {
                normalized = normalizer.NormalizeForMatching(normalized);
            }

            Assert.Equal(normalizer.NormalizeForMatching(input), normalized);
        }

        // ---------------------------------------------------------------
        // Constructor guard
        // ---------------------------------------------------------------

        [Fact]
        public void Constructor_NullLogger_ThrowsArgumentNullException()
        {
            Assert.Throws<System.ArgumentNullException>(() => new UnicodeNormalizer(null!));
        }
    }
}
