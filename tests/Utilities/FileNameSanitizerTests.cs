using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Utilities;

/// <summary>
/// Contract tests for <see cref="FileNameSanitizer"/>. The historical implementation
/// substituted any "<c>..</c>" substring with a space, which corrupted valid filenames like
/// <c>re..master.flac</c> (a legitimate filename pattern from streaming services tagging
/// remasters / re-releases). The fix narrows the substitution to standalone-dot segments
/// that would resolve as path traversal — embedded dots in normal filenames are preserved.
/// </summary>
public class FileNameSanitizerTests
{
    [Theory]
    [InlineData("re..master.flac", "re..master.flac")]
    [InlineData("track..remix.mp3", "track..remix.mp3")]
    [InlineData("Symphony No.5..2024.flac", "Symphony No.5..2024.flac")]
    [InlineData("Best of...Live!.mp3", "Best of...Live!.mp3")]
    public void SanitizeFileName_PreservesDotsEmbeddedInNames(string input, string expected)
    {
        var result = FileNameSanitizer.SanitizeFileName(input);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("....")]
    public void SanitizeFileName_PureDotSequences_AreNeutralized(string input)
    {
        // A filename that's nothing but dots is path-traversal-adjacent — neutralize to a
        // safe placeholder rather than passing through to the OS.
        var result = FileNameSanitizer.SanitizeFileName(input);

        Assert.NotEqual(input, result);
        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public void SanitizeFileName_RetainsExistingContract_InvalidCharsReplacedWithSpace()
    {
        // The Replace-with-space contract for invalid filename chars (": * ? \" < > |") is
        // preserved — readability beats deletion when a name like "AC/DC" would become "ACDC"
        // under a deletion-based sanitizer.
        var result = FileNameSanitizer.SanitizeFileName("AC/DC");

        Assert.Equal("AC DC", result);
    }

    [Fact]
    public void SanitizeFileName_EmptyOrWhitespace_ReturnsUnknown()
    {
        Assert.Equal("Unknown", FileNameSanitizer.SanitizeFileName(""));
        Assert.Equal("Unknown", FileNameSanitizer.SanitizeFileName("   "));
        Assert.Equal("Unknown", FileNameSanitizer.SanitizeFileName(null!));
    }

    [Fact]
    public void SanitizeFileName_WindowsReservedNames_PrefixedWithUnderscore()
    {
        Assert.Equal("_CON", FileNameSanitizer.SanitizeFileName("CON"));
        Assert.Equal("_NUL.txt", FileNameSanitizer.SanitizeFileName("NUL.txt"));
    }

    [Fact]
    public void SanitizePath_PreservesDotsEmbeddedInSegmentNames()
    {
        // Same regression case but at path level — SanitizePath splits, sanitizes each
        // segment via SanitizeFileName, and rejoins. So if SanitizeFileName preserves
        // dots, SanitizePath does too.
        var result = FileNameSanitizer.SanitizePath("Artist/Album/re..master.flac");

        Assert.Contains("re..master.flac", result);
    }
}
