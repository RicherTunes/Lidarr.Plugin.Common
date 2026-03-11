using System;
using System.Collections.Generic;
using System.Linq;
using Lidarr.Plugin.Abstractions.Models;

namespace Lidarr.Plugin.Common.TestKit.Validation;

/// <summary>
/// Validation helpers for StreamingModel objects in tests.
/// </summary>
public static class ModelValidationHelpers
{
    /// <summary>
    /// Validates that a StreamingArtist has all required fields populated.
    /// </summary>
    public static ValidationResult ValidateArtist(StreamingArtist? artist, bool requireComplete = false)
    {
        var errors = new List<string>();

        if (artist == null)
        {
            return ValidationResult.Failure("Artist is null");
        }

        if (string.IsNullOrWhiteSpace(artist.Id))
            errors.Add("Artist.Id is required");

        if (string.IsNullOrWhiteSpace(artist.Name))
            errors.Add("Artist.Name is required");

        if (requireComplete)
        {
            if (!artist.Genres.Any())
                errors.Add("Artist.Genres should not be empty for complete validation");

            if (!artist.ImageUrls.Any())
                errors.Add("Artist.ImageUrls should have at least one image");
        }

        return errors.Any()
            ? ValidationResult.Failure(errors.ToArray())
            : ValidationResult.Success();
    }

    /// <summary>
    /// Validates that a StreamingAlbum has all required fields populated.
    /// </summary>
    public static ValidationResult ValidateAlbum(StreamingAlbum? album, bool requireComplete = false)
    {
        var errors = new List<string>();

        if (album == null)
        {
            return ValidationResult.Failure("Album is null");
        }

        if (string.IsNullOrWhiteSpace(album.Id))
            errors.Add("Album.Id is required");

        if (string.IsNullOrWhiteSpace(album.Title))
            errors.Add("Album.Title is required");

        var artistResult = ValidateArtist(album.Artist);
        if (!artistResult.IsValid)
            errors.AddRange(artistResult.Errors.Select(e => $"Album.Artist: {e}"));

        if (album.TrackCount < 0)
            errors.Add("Album.TrackCount cannot be negative");

        if (requireComplete)
        {
            if (!album.ReleaseDate.HasValue)
                errors.Add("Album.ReleaseDate is required for complete validation");

            if (string.IsNullOrWhiteSpace(album.Label))
                errors.Add("Album.Label should be set for complete validation");

            if (!album.CoverArtUrls.Any())
                errors.Add("Album.CoverArtUrls should have at least one cover");

            if (!album.AvailableQualities.Any())
                errors.Add("Album.AvailableQualities should have at least one quality");
        }

        return errors.Any()
            ? ValidationResult.Failure(errors.ToArray())
            : ValidationResult.Success();
    }

    /// <summary>
    /// Validates that a StreamingTrack has all required fields populated.
    /// </summary>
    public static ValidationResult ValidateTrack(StreamingTrack? track, bool requireComplete = false)
    {
        var errors = new List<string>();

        if (track == null)
        {
            return ValidationResult.Failure("Track is null");
        }

        if (string.IsNullOrWhiteSpace(track.Id))
            errors.Add("Track.Id is required");

        if (string.IsNullOrWhiteSpace(track.Title))
            errors.Add("Track.Title is required");

        var artistResult = ValidateArtist(track.Artist);
        if (!artistResult.IsValid)
            errors.AddRange(artistResult.Errors.Select(e => $"Track.Artist: {e}"));

        if (requireComplete)
        {
            if (!track.TrackNumber.HasValue || track.TrackNumber < 1)
                errors.Add("Track.TrackNumber should be a positive integer");

            if (!track.Duration.HasValue || track.Duration.Value.TotalSeconds <= 0)
                errors.Add("Track.Duration should be positive");

            if (string.IsNullOrWhiteSpace(track.Isrc))
                errors.Add("Track.Isrc should be set for complete validation");
        }

        return errors.Any()
            ? ValidationResult.Failure(errors.ToArray())
            : ValidationResult.Success();
    }

    /// <summary>
    /// Validates that a StreamingQuality has valid specifications.
    /// </summary>
    public static ValidationResult ValidateQuality(StreamingQuality? quality)
    {
        var errors = new List<string>();

        if (quality == null)
        {
            return ValidationResult.Failure("Quality is null");
        }

        if (string.IsNullOrWhiteSpace(quality.Id))
            errors.Add("Quality.Id is required");

        if (string.IsNullOrWhiteSpace(quality.Format))
            errors.Add("Quality.Format is required");

        // Validate bitrate for lossy formats
        if (!quality.IsLossless && (!quality.Bitrate.HasValue || quality.Bitrate <= 0))
            errors.Add("Quality.Bitrate is required for lossy formats");

        // Validate sample rate and bit depth for lossless formats
        if (quality.IsLossless)
        {
            if (!quality.SampleRate.HasValue || quality.SampleRate <= 0)
                errors.Add("Quality.SampleRate is required for lossless formats");

            if (!quality.BitDepth.HasValue || quality.BitDepth <= 0)
                errors.Add("Quality.BitDepth is required for lossless formats");
        }

        // Check for reasonable values
        if (quality.SampleRate.HasValue && (quality.SampleRate < 8000 || quality.SampleRate > 384000))
            errors.Add("Quality.SampleRate should be between 8000 and 384000 Hz");

        if (quality.BitDepth.HasValue && (quality.BitDepth < 8 || quality.BitDepth > 32))
            errors.Add("Quality.BitDepth should be between 8 and 32");

        if (quality.Bitrate.HasValue && (quality.Bitrate < 32 || quality.Bitrate > 9216))
            errors.Add("Quality.Bitrate should be between 32 and 9216 kbps");

        return errors.Any()
            ? ValidationResult.Failure(errors.ToArray())
            : ValidationResult.Success();
    }

    /// <summary>
    /// Validates that a collection of tracks forms a valid album tracklist.
    /// </summary>
    public static ValidationResult ValidateTracklist(IEnumerable<StreamingTrack>? tracks)
    {
        var errors = new List<string>();

        if (tracks == null)
        {
            return ValidationResult.Failure("Tracklist is null");
        }

        var trackList = tracks.ToList();

        if (!trackList.Any())
        {
            return ValidationResult.Failure("Tracklist is empty");
        }

        // Validate each track
        for (int i = 0; i < trackList.Count; i++)
        {
            var trackResult = ValidateTrack(trackList[i]);
            if (!trackResult.IsValid)
            {
                errors.AddRange(trackResult.Errors.Select(e => $"Track[{i}]: {e}"));
            }
        }

        // Check for duplicate track numbers within each disc
        var tracksByDisc = trackList.GroupBy(t => t.DiscNumber ?? 1);
        foreach (var discGroup in tracksByDisc)
        {
            var duplicateTrackNumbers = discGroup
                .Where(t => t.TrackNumber.HasValue)
                .GroupBy(t => t.TrackNumber)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);

            foreach (var dup in duplicateTrackNumbers)
            {
                errors.Add($"Disc {discGroup.Key} has duplicate track number: {dup}");
            }
        }

        // Check for sequential track numbering
        foreach (var discGroup in tracksByDisc)
        {
            var trackNumbers = discGroup
                .Where(t => t.TrackNumber.HasValue)
                .Select(t => t.TrackNumber!.Value)
                .OrderBy(n => n)
                .ToList();

            if (trackNumbers.Any() && trackNumbers.First() != 1)
            {
                errors.Add($"Disc {discGroup.Key} tracklist should start at track 1, but starts at {trackNumbers.First()}");
            }

            for (int i = 1; i < trackNumbers.Count; i++)
            {
                if (trackNumbers[i] != trackNumbers[i - 1] + 1)
                {
                    errors.Add($"Disc {discGroup.Key} has gap in track numbering between {trackNumbers[i - 1]} and {trackNumbers[i]}");
                }
            }
        }

        return errors.Any()
            ? ValidationResult.Failure(errors.ToArray())
            : ValidationResult.Success();
    }

    /// <summary>
    /// Validates quality tier ordering (ensures qualities are properly ranked).
    /// </summary>
    public static ValidationResult ValidateQualityOrdering(IEnumerable<StreamingQuality>? qualities)
    {
        var errors = new List<string>();

        if (qualities == null)
        {
            return ValidationResult.Failure("Qualities collection is null");
        }

        var qualityList = qualities.ToList();

        if (!qualityList.Any())
        {
            return ValidationResult.Failure("Qualities collection is empty");
        }

        // Validate each quality
        foreach (var quality in qualityList)
        {
            var result = ValidateQuality(quality);
            if (!result.IsValid)
            {
                errors.AddRange(result.Errors.Select(e => $"{quality.Name ?? quality.Id}: {e}"));
            }
        }

        // Check tier ordering for fallback logic
        var tiers = qualityList.Select(q => q.GetTier()).ToList();
        var expectedOrder = tiers.OrderByDescending(t => t).ToList();

        if (!tiers.SequenceEqual(expectedOrder))
        {
            errors.Add("Quality list should be ordered from highest to lowest tier for proper fallback behavior");
        }

        return errors.Any()
            ? ValidationResult.Failure(errors.ToArray())
            : ValidationResult.Success();
    }

    /// <summary>
    /// Validates ISRC format (International Standard Recording Code).
    /// </summary>
    public static ValidationResult ValidateIsrc(string? isrc)
    {
        if (string.IsNullOrWhiteSpace(isrc))
            return ValidationResult.Failure("ISRC is empty");

        // ISRC format: CC-XXX-YY-NNNNN (12 characters without hyphens)
        var cleanIsrc = isrc.Replace("-", "").Replace(" ", "").ToUpperInvariant();

        if (cleanIsrc.Length != 12)
            return ValidationResult.Failure($"ISRC must be 12 characters (got {cleanIsrc.Length})");

        // First 2 characters: country code (letters)
        if (!cleanIsrc.Substring(0, 2).All(char.IsLetter))
            return ValidationResult.Failure("ISRC country code must be letters");

        // Characters 3-5: registrant code (alphanumeric)
        if (!cleanIsrc.Substring(2, 3).All(char.IsLetterOrDigit))
            return ValidationResult.Failure("ISRC registrant code must be alphanumeric");

        // Characters 6-7: year of reference (digits)
        if (!cleanIsrc.Substring(5, 2).All(char.IsDigit))
            return ValidationResult.Failure("ISRC year must be digits");

        // Characters 8-12: designation code (digits)
        if (!cleanIsrc.Substring(7, 5).All(char.IsDigit))
            return ValidationResult.Failure("ISRC designation code must be digits");

        return ValidationResult.Success();
    }

    /// <summary>
    /// Validates UPC/EAN barcode format.
    /// </summary>
    public static ValidationResult ValidateUpc(string? upc)
    {
        if (string.IsNullOrWhiteSpace(upc))
            return ValidationResult.Failure("UPC is empty");

        var cleanUpc = upc.Replace("-", "").Replace(" ", "");

        if (!cleanUpc.All(char.IsDigit))
            return ValidationResult.Failure("UPC must contain only digits");

        // UPC-A is 12 digits, EAN-13 is 13 digits
        if (cleanUpc.Length != 12 && cleanUpc.Length != 13)
            return ValidationResult.Failure($"UPC must be 12 or 13 digits (got {cleanUpc.Length})");

        // Validate check digit
        if (!ValidateUpcCheckDigit(cleanUpc))
            return ValidationResult.Failure("UPC check digit is invalid");

        return ValidationResult.Success();
    }

    private static bool ValidateUpcCheckDigit(string upc)
    {
        int sum = 0;
        bool isEan = upc.Length == 13;

        for (int i = 0; i < upc.Length - 1; i++)
        {
            int digit = upc[i] - '0';
            // EAN-13: odd positions × 1, even positions × 3
            // UPC-A: odd positions × 3, even positions × 1
            if (isEan)
                sum += (i % 2 == 0) ? digit : digit * 3;
            else
                sum += (i % 2 == 0) ? digit * 3 : digit;
        }

        int checkDigit = (10 - (sum % 10)) % 10;
        int actualCheckDigit = upc[^1] - '0';

        return checkDigit == actualCheckDigit;
    }
}

/// <summary>
/// Result of a validation operation.
/// </summary>
public sealed class ValidationResult
{
    public bool IsValid { get; }
    public IReadOnlyList<string> Errors { get; }

    private ValidationResult(bool isValid, IReadOnlyList<string> errors)
    {
        IsValid = isValid;
        Errors = errors;
    }

    public static ValidationResult Success() =>
        new(true, Array.Empty<string>());

    public static ValidationResult Failure(params string[] errors) =>
        new(false, errors);

    public string GetErrorSummary(string separator = "; ") =>
        string.Join(separator, Errors);

    /// <summary>
    /// Throws if validation failed.
    /// </summary>
    public void ThrowIfInvalid()
    {
        if (!IsValid)
            throw new ValidationException(GetErrorSummary());
    }
}

/// <summary>
/// Exception thrown when validation fails.
/// </summary>
public class ValidationException : Exception
{
    public ValidationException(string message) : base(message) { }
}
