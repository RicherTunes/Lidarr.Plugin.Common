using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Utilities;

/// <summary>
/// #21 (LOOP-010): the standalone Levenshtein-distance / Jaro / Jaro-Winkler primitives were duplicated
/// in qobuzarr (StringSimilarity.cs) and brainarr (AdvancedDuplicateDetector + SimpleRecommendationValidator)
/// with byte-identical logic. These tests pin the canonical behavior so both plugins can delegate to Common.
/// </summary>
public class StringSimilarityTests
{
    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("flaw", "lawn", 2)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    [InlineData("", "", 0)]
    [InlineData("same", "same", 0)]
    public void LevenshteinDistance_KnownPairs(string a, string b, int expected)
    {
        Assert.Equal(expected, StringSimilarity.LevenshteinDistance(a, b));
    }

    [Fact]
    public void Jaro_ClassicMarthaMarhta_IsAboutPoint944()
    {
        var j = StringSimilarity.Jaro("MARTHA", "MARHTA");
        Assert.InRange(j, 0.943, 0.945);
    }

    [Fact]
    public void Jaro_IdenticalIsOne_DisjointIsZero()
    {
        Assert.Equal(1.0, StringSimilarity.Jaro("abc", "abc"));
        Assert.Equal(0.0, StringSimilarity.Jaro("abc", "xyz"));
    }

    [Fact]
    public void JaroWinkler_GivesPrefixBonusOverJaro()
    {
        // MARTHA/MARHTA share a 3-char prefix, so Jaro-Winkler > Jaro (classic ~0.961).
        var jw = StringSimilarity.JaroWinkler("MARTHA", "MARHTA");
        Assert.InRange(jw, 0.960, 0.962);
        Assert.True(jw > StringSimilarity.Jaro("MARTHA", "MARHTA"));
    }

    [Fact]
    public void JaroWinkler_IdenticalIsOne()
    {
        Assert.Equal(1.0, StringSimilarity.JaroWinkler("Nevermind", "Nevermind"));
    }

    [Fact]
    public void JaroWinkler_BelowThresholdReturnsPlainJaro()
    {
        // When Jaro < 0.7 the prefix bonus is not applied (returns plain Jaro).
        var a = "abcd"; var b = "wxyz";
        Assert.Equal(StringSimilarity.Jaro(a, b), StringSimilarity.JaroWinkler(a, b));
    }
}
