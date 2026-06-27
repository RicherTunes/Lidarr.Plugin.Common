using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Utilities;

[Trait("Category", "Unit")]
public class StringUtilitiesTests
{
    // Built from codepoints so the test is byte-exact regardless of source-file encoding/normalization.
    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600);   // 😀 — astral, 2 UTF-16 units
    private static readonly string Acute = ((char)0x0301).ToString();        // combining acute accent

    [Theory]
    [InlineData("", 5, "")]
    [InlineData("abc", 5, "abc")]      // already fits
    [InlineData("abc", 3, "abc")]      // exactly fits
    [InlineData("abcdef", 3, "abc")]   // plain ascii cut
    public void TruncateGraphemeSafe_basicCases(string input, int max, string expected)
    {
        Assert.Equal(expected, StringUtilities.TruncateGraphemeSafe(input, max));
    }

    [Fact]
    public void TruncateGraphemeSafe_neverSplitsSurrogatePair()
    {
        var s = "ab" + Emoji + "cd";
        Assert.Equal("ab", StringUtilities.TruncateGraphemeSafe(s, 3));         // cut lands inside the pair
        Assert.Equal("ab" + Emoji, StringUtilities.TruncateGraphemeSafe(s, 4)); // whole pair fits
    }

    [Fact]
    public void TruncateGraphemeSafe_neverSplitsCombiningSequence()
    {
        // 'e' + U+0301 is ONE grapheme spanning 2 UTF-16 units.
        var decomposed = "e" + Acute + "x";
        Assert.Equal("", StringUtilities.TruncateGraphemeSafe(decomposed, 1));            // can't hold the 2-unit grapheme
        Assert.Equal("e" + Acute, StringUtilities.TruncateGraphemeSafe(decomposed, 2));   // whole grapheme fits
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TruncateGraphemeSafe_nonPositiveBudget_returnsEmpty(int max)
    {
        Assert.Equal("", StringUtilities.TruncateGraphemeSafe("abc", max));
    }

    [Fact]
    public void TruncateGraphemeSafe_null_returnsEmpty()
    {
        Assert.Equal("", StringUtilities.TruncateGraphemeSafe(null, 5));
    }
}
