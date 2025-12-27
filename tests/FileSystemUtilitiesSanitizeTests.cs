using Xunit;
using Lidarr.Plugin.Common.Utilities;

namespace Lidarr.Plugin.Common.Tests;

/// <summary>
/// Edge case tests for SanitizeFileName to lock down behavior.
/// These document the actual current behavior as a regression gate.
/// </summary>
public class FileSystemUtilitiesSanitizeTests
{
    // FileNameSanitizer returns "Unknown" for empty/whitespace inputs
    [Theory]
    [InlineData("   ", "Unknown")]              // Whitespace-only -> Unknown (from FileNameSanitizer)
    [InlineData("", "Unknown")]                 // Empty string -> Unknown
    [InlineData(null, "Unknown")]               // Null -> Unknown
    public void SanitizeFileName_WhitespaceOrEmpty_ReturnsUnknown(string? input, string expected)
    {
        var result = FileSystemUtilities.SanitizeFileName(input!);
        Assert.Equal(expected, result);
    }

    // All-invalid inputs also return Unknown (invalid chars -> spaces -> trim -> empty -> Unknown)
    [Theory]
    [InlineData("???", "Unknown")]              // All invalid chars -> Unknown
    [InlineData("***", "Unknown")]              // Asterisks only -> Unknown
    public void SanitizeFileName_AllInvalidChars_ReturnsUnknown(string input, string expected)
    {
        var result = FileSystemUtilities.SanitizeFileName(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("CON", "_CON")]                 // Reserved name
    [InlineData("PRN", "_PRN")]                 // Reserved name
    [InlineData("AUX", "_AUX")]                 // Reserved name
    [InlineData("NUL", "_NUL")]                 // Reserved name
    [InlineData("COM1", "_COM1")]               // Reserved name
    [InlineData("LPT1", "_LPT1")]               // Reserved name
    public void SanitizeFileName_ReservedNames_AddPrefix(string input, string expected)
    {
        var result = FileSystemUtilities.SanitizeFileName(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Title.", "Title")]             // Trailing dot stripped
    [InlineData("Title..", "Title")]            // Multiple trailing dots
    [InlineData("Title   ", "Title")]           // Trailing spaces stripped
    [InlineData("Title._-", "Title")]           // Mixed trailing chars
    [InlineData("Title. ", "Title")]            // Dot + space
    public void SanitizeFileName_TrailingChars_Stripped(string input, string expected)
    {
        var result = FileSystemUtilities.SanitizeFileName(input);
        Assert.Equal(expected, result);
    }

    // FIXED: Reserved name check now runs AFTER TrimEnd
    // "CON." -> trim to "CON" -> reserved check adds prefix -> "_CON"
    [Theory]
    [InlineData("CON.", "_CON")]                // Reserved + trailing dot: now correctly prefixed
    [InlineData("CON...", "_CON")]              // Multiple trailing dots
    [InlineData("AUX   ", "_AUX")]              // Reserved + trailing spaces
    [InlineData("NUL._", "_NUL")]               // Reserved + mixed trailing
    [InlineData("prn.", "_prn")]                // Case insensitive
    [InlineData("COM1.txt", "COM1.txt")]        // Not reserved (has extension content)
    public void SanitizeFileName_ReservedWithTrailing_CorrectlyPrefixed(string input, string expected)
    {
        var result = FileSystemUtilities.SanitizeFileName(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Valid Title", "Valid Title")]
    [InlineData("Artist - Album", "Artist - Album")]
    [InlineData("Track (Remix)", "Track (Remix)")]
    public void SanitizeFileName_ValidInput_Unchanged(string input, string expected)
    {
        var result = FileSystemUtilities.SanitizeFileName(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Hello<World>", "Hello World")]     // Invalid chars replaced with space
    [InlineData("Title:Subtitle", "Title Subtitle")] // Colon replaced
    public void SanitizeFileName_InvalidChars_ReplacedWithSpace(string input, string expected)
    {
        var result = FileSystemUtilities.SanitizeFileName(input);
        Assert.Equal(expected, result);
    }
}
