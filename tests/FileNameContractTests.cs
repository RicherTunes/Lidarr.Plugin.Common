using System.Text;
using Xunit;
using Lidarr.Plugin.Common.Utilities;

namespace Lidarr.Plugin.Common.Tests;

/// <summary>
/// Contract tests for filename generation across the plugin ecosystem.
/// These tests define the exact output format expected by Tidalarr, Qobuzarr,
/// Brainarr, and AppleMusicarr to ensure cross-plugin parity.
///
/// IMPORTANT: Do not change these tests without updating all plugins.
/// They document the "output contract" that prevents drift.
/// </summary>
public class FileNameContractTests
{
    #region Unicode NFC Normalization

    /// <summary>
    /// Verifies that decomposed Unicode characters (NFD) are normalized to composed form (NFC).
    /// This prevents cross-OS filename mismatches (macOS uses NFD, Windows uses NFC).
    /// </summary>
    [Theory]
    [InlineData("café", "café")]                           // NFD é (e + combining acute) → NFC é
    [InlineData("naïve", "naïve")]                         // NFD ï → NFC ï
    [InlineData("Björk", "Björk")]                         // NFD ö → NFC ö
    [InlineData("Sigur Rós", "Sigur Rós")]                 // NFD ó → NFC ó
    [InlineData("Déjà Vu", "Déjà Vu")]                     // Multiple diacritics
    public void SanitizeFileName_NormalizesToNFC(string input, string expected)
    {
        // Arrange: Create NFD version of input (decomposed)
        var nfdInput = input.Normalize(NormalizationForm.FormD);

        // Act
        var result = FileSystemUtilities.SanitizeFileName(nfdInput);

        // Assert: Result should be NFC (composed)
        Assert.True(result.IsNormalized(NormalizationForm.FormC),
            $"Result '{result}' should be NFC normalized");
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Verifies NFC normalization is applied in track filenames.
    /// </summary>
    [Fact]
    public void CreateTrackFileName_NormalizesUnicodeToNFC()
    {
        // Arrange: NFD decomposed "café" (e + combining acute accent)
        var nfdTitle = "café".Normalize(NormalizationForm.FormD);

        // Act
        var result = FileSystemUtilities.CreateTrackFileName(nfdTitle, 1, "flac");

        // Assert: Title in filename should be NFC
        Assert.Contains("café", result);
        Assert.True(result.IsNormalized(NormalizationForm.FormC));
    }

    /// <summary>
    /// Tests various Unicode scripts are preserved correctly.
    /// </summary>
    [Theory]
    [InlineData("日本語タイトル", "日本語タイトル")]       // Japanese
    [InlineData("한국어 제목", "한국어 제목")]             // Korean
    [InlineData("Название", "Название")]                   // Cyrillic
    [InlineData("عنوان عربي", "عنوان عربي")]               // Arabic
    [InlineData("שם בעברית", "שם בעברית")]                 // Hebrew
    [InlineData("中文标题", "中文标题")]                   // Chinese
    public void SanitizeFileName_PreservesInternationalCharacters(string input, string expected)
    {
        var result = FileSystemUtilities.SanitizeFileName(input);
        Assert.Equal(expected, result);
    }

    #endregion

    #region Multi-Disc Naming Contract (DxxTxx Format)

    /// <summary>
    /// Contract: Multi-disc albums use DxxTxx prefix (disc number, then track number).
    /// Both disc and track numbers are zero-padded to 2 digits.
    /// </summary>
    [Theory]
    // Standard multi-disc cases
    [InlineData(1, 1, 2, "D01T01")]   // Disc 1, Track 1, 2-disc album
    [InlineData(1, 2, 2, "D02T01")]   // Disc 2, Track 1, 2-disc album
    [InlineData(10, 1, 2, "D01T10")]  // Track 10, Disc 1
    [InlineData(99, 3, 5, "D03T99")]  // Track 99, Disc 3 of 5
    // Edge cases
    [InlineData(1, 10, 10, "D10T01")] // 10+ disc album
    [InlineData(1, 1, 99, "D01T01")]  // 99-disc album (extreme case)
    public void CreateTrackFileName_MultiDiscPrefix_Format(int trackNum, int discNum, int totalDiscs, string expectedPrefix)
    {
        var result = FileSystemUtilities.CreateTrackFileName(
            title: "Title",
            trackNumber: trackNum,
            extension: "flac",
            discNumber: discNum,
            totalDiscs: totalDiscs);

        Assert.StartsWith(expectedPrefix, result);
    }

    /// <summary>
    /// Contract: Single-disc albums use simple track number prefix (no disc indicator).
    /// </summary>
    [Theory]
    [InlineData(1, "01 - ")]
    [InlineData(5, "05 - ")]
    [InlineData(10, "10 - ")]
    [InlineData(99, "99 - ")]
    public void CreateTrackFileName_SingleDiscPrefix_Format(int trackNum, string expectedPrefix)
    {
        var result = FileSystemUtilities.CreateTrackFileName(
            title: "Title",
            trackNumber: trackNum,
            extension: "flac",
            discNumber: 1,
            totalDiscs: 1);

        Assert.StartsWith(expectedPrefix, result);
    }

    /// <summary>
    /// Contract: Full filename format for multi-disc is "DxxTxx - Title.ext"
    /// </summary>
    [Fact]
    public void CreateTrackFileName_MultiDisc_FullFormat()
    {
        var result = FileSystemUtilities.CreateTrackFileName(
            title: "My Track Title",
            trackNumber: 5,
            extension: "flac",
            discNumber: 2,
            totalDiscs: 3);

        Assert.Equal("D02T05 - My Track Title.flac", result);
    }

    /// <summary>
    /// Contract: Full filename format for single-disc is "xx - Title.ext"
    /// </summary>
    [Fact]
    public void CreateTrackFileName_SingleDisc_FullFormat()
    {
        var result = FileSystemUtilities.CreateTrackFileName(
            title: "My Track Title",
            trackNumber: 5,
            extension: "flac",
            discNumber: 1,
            totalDiscs: 1);

        Assert.Equal("05 - My Track Title.flac", result);
    }

    #endregion

    #region Extension Normalization Contract

    /// <summary>
    /// Contract: Extensions are normalized - leading dots stripped, whitespace trimmed,
    /// empty/null defaults to "flac".
    /// </summary>
    [Theory]
    // Normal cases
    [InlineData("flac", "flac")]
    [InlineData("m4a", "m4a")]
    [InlineData("mp3", "mp3")]
    [InlineData("ogg", "ogg")]
    [InlineData("opus", "opus")]
    // Leading dot normalization
    [InlineData(".flac", "flac")]
    [InlineData("..flac", "flac")]
    [InlineData("...flac", "flac")]
    // Whitespace normalization
    [InlineData(" flac", "flac")]
    [InlineData("flac ", "flac")]
    [InlineData(" flac ", "flac")]
    [InlineData(" .flac ", "flac")]
    // Defaults
    [InlineData("", "flac")]
    [InlineData(null, "flac")]
    [InlineData("   ", "flac")]
    public void CreateTrackFileName_ExtensionNormalization(string? extension, string expectedExtension)
    {
        var result = FileSystemUtilities.CreateTrackFileName(
            title: "Title",
            trackNumber: 1,
            extension: extension!,
            discNumber: 1,
            totalDiscs: 1);

        Assert.EndsWith($".{expectedExtension}", result);
    }

    /// <summary>
    /// Contract: Extension case is preserved (not lowercased).
    /// </summary>
    [Theory]
    [InlineData("FLAC", "FLAC")]
    [InlineData("M4A", "M4A")]
    [InlineData("Flac", "Flac")]
    public void CreateTrackFileName_ExtensionCasePreserved(string extension, string expectedExtension)
    {
        var result = FileSystemUtilities.CreateTrackFileName(
            title: "Title",
            trackNumber: 1,
            extension: extension,
            discNumber: 1,
            totalDiscs: 1);

        Assert.EndsWith($".{expectedExtension}", result);
    }

    #endregion

    #region Album Directory Naming Contract

    /// <summary>
    /// Contract: Album directory format is "Title (Year)" when year is provided.
    /// </summary>
    [Theory]
    [InlineData("Album Title", 2024, "Album Title (2024)")]
    [InlineData("Album Title", 1999, "Album Title (1999)")]
    [InlineData("Greatest Hits", 2000, "Greatest Hits (2000)")]
    public void CreateAlbumDirectoryName_WithYear_Format(string title, int year, string expected)
    {
        var result = FileSystemUtilities.CreateAlbumDirectoryName(title, year);
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Contract: Album directory without year is just the title.
    /// </summary>
    [Fact]
    public void CreateAlbumDirectoryName_WithoutYear_JustTitle()
    {
        var result = FileSystemUtilities.CreateAlbumDirectoryName("Album Title");
        Assert.Equal("Album Title", result);
    }

    /// <summary>
    /// Contract: Album directory with null year is just the title.
    /// </summary>
    [Fact]
    public void CreateAlbumDirectoryName_NullYear_JustTitle()
    {
        var result = FileSystemUtilities.CreateAlbumDirectoryName("Album Title", null);
        Assert.Equal("Album Title", result);
    }

    #endregion

    #region Filename Sanitization Contract

    /// <summary>
    /// Contract: Invalid filesystem characters are replaced with spaces.
    /// </summary>
    [Theory]
    [InlineData("Title<>Name", "Title Name")]       // Angle brackets (spaces collapsed)
    [InlineData("Title:Name", "Title Name")]        // Colon
    [InlineData("Title\"Name", "Title Name")]       // Quote
    [InlineData("Title/Name", "Title Name")]        // Forward slash
    [InlineData("Title\\Name", "Title Name")]       // Backslash
    [InlineData("Title|Name", "Title Name")]        // Pipe
    [InlineData("Title?Name", "Title Name")]        // Question mark
    [InlineData("Title*Name", "Title Name")]        // Asterisk
    public void SanitizeFileName_InvalidChars_Replaced(string input, string expected)
    {
        var result = FileSystemUtilities.SanitizeFileName(input);
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Contract: Windows reserved names get underscore prefix.
    /// </summary>
    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("PRN", "_PRN")]
    [InlineData("AUX", "_AUX")]
    [InlineData("NUL", "_NUL")]
    [InlineData("COM1", "_COM1")]
    [InlineData("COM9", "_COM9")]
    [InlineData("LPT1", "_LPT1")]
    [InlineData("LPT9", "_LPT9")]
    // Case insensitive
    [InlineData("con", "_con")]
    [InlineData("Con", "_Con")]
    // Reserved base names remain reserved even with extensions (Windows portability)
    [InlineData("NUL.txt", "_NUL.txt")]
    [InlineData("prn.doc", "_prn.doc")]
    [InlineData("COM1.txt", "_COM1.txt")]
    [InlineData("LPT9.doc", "_LPT9.doc")]
    public void SanitizeFileName_ReservedNames_Prefixed(string input, string expected)
    {
        var result = FileSystemUtilities.SanitizeFileName(input);
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Contract: Trailing dots and spaces are stripped (Windows limitation).
    /// </summary>
    [Theory]
    [InlineData("Title.", "Title")]
    [InlineData("Title..", "Title")]
    [InlineData("Title...", "Title")]
    [InlineData("Title   ", "Title")]
    [InlineData("Title. ", "Title")]
    [InlineData("Title .", "Title")]
    public void SanitizeFileName_TrailingChars_Stripped(string input, string expected)
    {
        var result = FileSystemUtilities.SanitizeFileName(input);
        Assert.Equal(expected, result);
    }

    #endregion

    #region Path Sanitization Contract

    /// <summary>
    /// Contract: Directory paths have each segment sanitized independently.
    /// </summary>
    [Fact]
    public void SanitizeDirectoryPath_EachSegmentSanitized()
    {
        var result = FileSystemUtilities.SanitizeDirectoryPath("Artist:Name/Album<Title>");

        // Each segment should be sanitized
        Assert.DoesNotContain(":", result);
        Assert.DoesNotContain("<", result);
        Assert.DoesNotContain(">", result);
    }

    /// <summary>
    /// Contract: Empty/whitespace path returns "Unknown".
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SanitizeDirectoryPath_EmptyOrWhitespace_ReturnsUnknown(string? input)
    {
        var result = FileSystemUtilities.SanitizeDirectoryPath(input!);
        Assert.Equal("Unknown", result);
    }

    #endregion

    #region Length Limits Contract

    /// <summary>
    /// Contract: Filenames are truncated to maxLength while preserving word boundaries.
    /// </summary>
    [Fact]
    public void SanitizeFileName_LongInput_TruncatedAtWordBoundary()
    {
        var longTitle = "This Is A Very Long Title That Exceeds The Maximum Length Allowed For Filenames";
        var result = FileSystemUtilities.SanitizeFileName(longTitle, maxLength: 50);

        Assert.True(result.Length <= 50);
        // Should truncate at word boundary, not mid-word
        Assert.DoesNotContain("Filenam", result); // No partial words at the end
    }

    /// <summary>
    /// Contract: Track filename respects maxLength including prefix and extension.
    /// </summary>
    [Fact]
    public void CreateTrackFileName_LongTitle_TruncatedWithPrefixAndExtension()
    {
        var longTitle = new string('A', 250);
        var result = FileSystemUtilities.CreateTrackFileName(longTitle, 1, "flac", maxLength: 200);

        Assert.True(result.Length <= 200);
        Assert.StartsWith("01 - ", result);
        Assert.EndsWith(".flac", result);
    }

    #endregion
}
