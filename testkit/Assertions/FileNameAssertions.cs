using System;
using System.IO;
using System.Text;

namespace Lidarr.Plugin.Common.TestKit.Assertions;

/// <summary>
/// Assertion helpers for validating file naming contracts across streaming plugins.
/// Ensures consistent naming behavior for multi-disc formatting, Unicode normalization,
/// and file system safety.
/// </summary>
public static class FileNameAssertions
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private static readonly string[] ReservedNames = new[]
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Asserts that the filename is normalized to Unicode NFC (Composed) form.
    /// This ensures consistent string comparison and storage across platforms.
    /// </summary>
    /// <param name="fileName">The filename to check.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    public static void AssertNormalizedToFormC(string fileName, string? because = null)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            throw new FileNameAssertionException("Filename was null or empty.");
        }

        if (!fileName.IsNormalized(NormalizationForm.FormC))
        {
            var message = $"Expected filename to be normalized to Unicode FormC (NFC)";
            if (!string.IsNullOrEmpty(because))
            {
                message += $" because {because}";
            }
            message += $", but it was not. Filename: '{fileName}'";
            throw new FileNameAssertionException(message);
        }
    }

    /// <summary>
    /// Asserts that the filename contains no invalid file system characters.
    /// </summary>
    /// <param name="fileName">The filename to check.</param>
    public static void AssertNoInvalidCharacters(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            throw new FileNameAssertionException("Filename was null or empty.");
        }

        foreach (var invalidChar in InvalidFileNameChars)
        {
            if (fileName.Contains(invalidChar))
            {
                throw new FileNameAssertionException(
                    $"Filename contains invalid character '{invalidChar}' (U+{(int)invalidChar:X4}). Filename: '{fileName}'");
            }
        }
    }

    /// <summary>
    /// Asserts that the filename does not use Windows reserved names (CON, PRN, AUX, etc.).
    /// </summary>
    /// <param name="fileName">The filename to check.</param>
    public static void AssertNoReservedNames(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            throw new FileNameAssertionException("Filename was null or empty.");
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);

        foreach (var reserved in ReservedNames)
        {
            if (string.Equals(baseName, reserved, StringComparison.OrdinalIgnoreCase))
            {
                throw new FileNameAssertionException(
                    $"Filename uses reserved name '{reserved}'. Filename: '{fileName}'");
            }
        }
    }

    /// <summary>
    /// Asserts that the filename uses the standard multi-disc prefix format: DxxTxx -
    /// </summary>
    /// <param name="fileName">The filename to check.</param>
    /// <param name="expectedDisc">Expected disc number.</param>
    /// <param name="expectedTrack">Expected track number.</param>
    public static void AssertMultiDiscPrefix(string fileName, int expectedDisc, int expectedTrack)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            throw new FileNameAssertionException("Filename was null or empty.");
        }

        var expectedPrefix = $"D{expectedDisc:D2}T{expectedTrack:D2} - ";
        if (!fileName.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            throw new FileNameAssertionException(
                $"Expected multi-disc prefix '{expectedPrefix}', but filename was '{fileName}'");
        }
    }

    /// <summary>
    /// Asserts that the filename uses the standard single-disc track prefix: xx -
    /// </summary>
    /// <param name="fileName">The filename to check.</param>
    /// <param name="expectedTrack">Expected track number.</param>
    public static void AssertSingleDiscPrefix(string fileName, int expectedTrack)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            throw new FileNameAssertionException("Filename was null or empty.");
        }

        var expectedPrefix = $"{expectedTrack:D2} - ";
        if (!fileName.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            throw new FileNameAssertionException(
                $"Expected single-disc prefix '{expectedPrefix}', but filename was '{fileName}'");
        }
    }

    /// <summary>
    /// Asserts that the filename has the expected extension (case-insensitive).
    /// </summary>
    /// <param name="fileName">The filename to check.</param>
    /// <param name="expectedExtension">Expected extension (e.g., ".flac", ".mp3").</param>
    public static void AssertExtension(string fileName, string expectedExtension)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            throw new FileNameAssertionException("Filename was null or empty.");
        }

        var actualExtension = Path.GetExtension(fileName);
        if (!string.Equals(actualExtension, expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNameAssertionException(
                $"Expected extension '{expectedExtension}', but was '{actualExtension}'. Filename: '{fileName}'");
        }
    }

    /// <summary>
    /// Performs all standard file naming contract validations.
    /// </summary>
    /// <param name="fileName">The filename to validate.</param>
    public static void AssertValidFileName(string fileName)
    {
        AssertNormalizedToFormC(fileName);
        AssertNoInvalidCharacters(fileName);
        AssertNoReservedNames(fileName);
    }
}

/// <summary>Thrown when a file name assertion fails.</summary>
public sealed class FileNameAssertionException : Exception
{
    public FileNameAssertionException(string message) : base(message)
    {
    }
}
