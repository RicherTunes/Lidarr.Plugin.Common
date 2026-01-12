using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Lidarr.Plugin.Abstractions.Models;

namespace Lidarr.Plugin.Common.TestKit.Validation;

/// <summary>
/// ReleaseDate parity validation helpers and test utilities.
/// Tier-2 characterization tests (log-only, non-failing) for documenting
/// cross-plugin ReleaseDate handling behavior.
/// </summary>
/// <remarks>
/// <para><b>Policy: Album release dates are calendar dates (no timezone).</b></para>
/// <para>
/// Streaming services publish album release dates as simple calendar dates (e.g., "2024-01-15")
/// without timezone information. We normalize to DateTimeKind.Unspecified to avoid accidental
/// timezone drift when comparing dates from different sources.
/// </para>
/// </remarks>
public static class ReleaseDateParityTests
{
    /// <summary>
    /// Normalizes a DateTime to date-only with Unspecified kind.
    /// This is the canonical policy: album release dates are calendar dates (no timezone).
    /// </summary>
    /// <remarks>
    /// Uses DateTimeKind.Unspecified to prevent timezone conversion issues.
    /// Album release dates from streaming APIs are typically just YYYY-MM-DD.
    /// </remarks>
    public static DateTime? NormalizeToDateOnly(DateTime? dateTime)
    {
        if (!dateTime.HasValue)
            return null;

        // Strip time and normalize to Unspecified kind (calendar date)
        var date = dateTime.Value.Date;
        return DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
    }

    /// <summary>
    /// Compares two release dates using date-only semantics.
    /// Returns true if both dates represent the same calendar date.
    /// </summary>
    public static bool AreDatesEqual(DateTime? date1, DateTime? date2)
    {
        if (!date1.HasValue && !date2.HasValue)
            return true;

        if (!date1.HasValue || !date2.HasValue)
            return false;

        return date1.Value.Date == date2.Value.Date;
    }

    /// <summary>
    /// Validates that a release date follows the date-only policy.
    /// Returns characterization info (not an error) if time component is non-zero.
    /// </summary>
    public static ReleaseDateCharacterization CharacterizeReleaseDate(DateTime? releaseDate, string source)
    {
        if (!releaseDate.HasValue)
        {
            return new ReleaseDateCharacterization
            {
                Source = source,
                RawValue = null,
                NormalizedValue = null,
                HasTimeComponent = false,
                IsNullOrMissing = true,
                Notes = "ReleaseDate is null/missing"
            };
        }

        var dt = releaseDate.Value;
        var hasTime = dt.TimeOfDay != TimeSpan.Zero;
        var kind = dt.Kind;
        var normalizedDate = DateTime.SpecifyKind(dt.Date, DateTimeKind.Unspecified);

        var kindNote = kind switch
        {
            DateTimeKind.Utc => "UTC",
            DateTimeKind.Local => "Local (potential timezone drift risk)",
            _ => "Unspecified (calendar date)"
        };

        return new ReleaseDateCharacterization
        {
            Source = source,
            RawValue = dt,
            NormalizedValue = normalizedDate,
            HasTimeComponent = hasTime,
            IsNullOrMissing = false,
            DateTimeKind = kind,
            Notes = hasTime
                ? $"Has time component: {dt:HH:mm:ss.fff} Kind={kindNote} (normalized to {normalizedDate:yyyy-MM-dd})"
                : $"Clean date-only: {dt:yyyy-MM-dd} Kind={kindNote}"
        };
    }

    /// <summary>
    /// Characterizes a StreamingAlbum's release date handling.
    /// </summary>
    public static ReleaseDateCharacterization CharacterizeAlbum(StreamingAlbum? album, string source)
    {
        if (album == null)
        {
            return new ReleaseDateCharacterization
            {
                Source = source,
                RawValue = null,
                NormalizedValue = null,
                HasTimeComponent = false,
                IsNullOrMissing = true,
                Notes = "Album is null"
            };
        }

        var charResult = CharacterizeReleaseDate(album.ReleaseDate, source);

        // Check for legacy metadata duplication
        if (album.Metadata.TryGetValue("release_date", out var legacyValue))
        {
            charResult.HasLegacyMetadataField = true;
            charResult.LegacyMetadataValue = legacyValue;
            charResult.Notes += $" | Legacy Metadata['release_date'] = {legacyValue}";
        }

        return charResult;
    }

    /// <summary>
    /// Creates a test report comparing release dates across plugins.
    /// For use in Tier-2 log-only characterization tests.
    /// </summary>
    public static ReleaseDateParityReport CreateParityReport(
        string albumId,
        string albumTitle,
        IEnumerable<(string PluginName, DateTime? ReleaseDate)> pluginResults)
    {
        var report = new ReleaseDateParityReport
        {
            AlbumId = albumId,
            AlbumTitle = albumTitle,
            Characterizations = []
        };

        DateTime? normalizedReference = null;
        bool hasParityIssue = false;

        foreach (var (pluginName, releaseDate) in pluginResults)
        {
            var charResult = CharacterizeReleaseDate(releaseDate, pluginName);
            report.Characterizations.Add(charResult);

            if (charResult.NormalizedValue.HasValue)
            {
                if (!normalizedReference.HasValue)
                {
                    normalizedReference = charResult.NormalizedValue;
                }
                else if (normalizedReference.Value != charResult.NormalizedValue.Value)
                {
                    hasParityIssue = true;
                }
            }
        }

        report.HasParityIssue = hasParityIssue;
        report.NormalizedConsensus = normalizedReference;

        return report;
    }
}

/// <summary>
/// Characterization result for a single ReleaseDate value.
/// Used for Tier-2 logging and parity tracking.
/// </summary>
public class ReleaseDateCharacterization
{
    /// <summary>Source plugin or service name.</summary>
    public required string Source { get; init; }

    /// <summary>Raw DateTime value as received from the plugin.</summary>
    public DateTime? RawValue { get; init; }

    /// <summary>Normalized date-only value.</summary>
    public DateTime? NormalizedValue { get; init; }

    /// <summary>Whether the raw value included a time component.</summary>
    public bool HasTimeComponent { get; init; }

    /// <summary>Whether the value was null or missing.</summary>
    public bool IsNullOrMissing { get; init; }

    /// <summary>DateTimeKind of the raw value (UTC, Local, or Unspecified).</summary>
    public DateTimeKind DateTimeKind { get; init; }

    /// <summary>Whether the legacy Metadata["release_date"] field exists.</summary>
    public bool HasLegacyMetadataField { get; set; }

    /// <summary>Value from legacy Metadata["release_date"] if present.</summary>
    public object? LegacyMetadataValue { get; set; }

    /// <summary>Human-readable characterization notes.</summary>
    public string Notes { get; set; } = string.Empty;

    public override string ToString() =>
        $"[{Source}] {(IsNullOrMissing ? "NULL" : $"{NormalizedValue:yyyy-MM-dd}")}{(HasTimeComponent ? " (had time)" : "")}";
}

/// <summary>
/// Parity report comparing ReleaseDate across multiple plugins.
/// </summary>
public class ReleaseDateParityReport
{
    /// <summary>Album identifier.</summary>
    public required string AlbumId { get; init; }

    /// <summary>Album title for human readability.</summary>
    public required string AlbumTitle { get; init; }

    /// <summary>Characterization from each plugin.</summary>
    public required List<ReleaseDateCharacterization> Characterizations { get; init; }

    /// <summary>Whether plugins disagree on the normalized date.</summary>
    public bool HasParityIssue { get; set; }

    /// <summary>Consensus normalized date (if all plugins agree).</summary>
    public DateTime? NormalizedConsensus { get; set; }

    /// <summary>Generates a log-friendly summary.</summary>
    public string ToLogSummary()
    {
        var lines = new List<string>
        {
            $"=== ReleaseDate Parity Report ===",
            $"Album: {AlbumTitle} ({AlbumId})",
            $"Parity: {(HasParityIssue ? "MISMATCH" : "OK")}",
            $"Consensus: {(NormalizedConsensus.HasValue ? NormalizedConsensus.Value.ToString("yyyy-MM-dd") : "N/A")}",
            $"---"
        };

        foreach (var c in Characterizations)
        {
            lines.Add(c.ToString());
            if (c.HasLegacyMetadataField)
            {
                lines.Add($"  └─ Legacy metadata: {c.LegacyMetadataValue}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Known album release date reference data for characterization tests.
/// These are well-known albums with verified release dates.
/// </summary>
public static class KnownAlbumReleaseDates
{
    /// <summary>
    /// Miles Davis - Kind of Blue (August 17, 1959)
    /// </summary>
    public static readonly DateTime KindOfBlue = new(1959, 8, 17);

    /// <summary>
    /// Pink Floyd - The Dark Side of the Moon (March 1, 1973)
    /// </summary>
    public static readonly DateTime DarkSideOfTheMoon = new(1973, 3, 1);

    /// <summary>
    /// Radiohead - OK Computer (May 21, 1997)
    /// </summary>
    public static readonly DateTime OkComputer = new(1997, 5, 21);

    /// <summary>
    /// Daft Punk - Random Access Memories (May 17, 2013)
    /// </summary>
    public static readonly DateTime RandomAccessMemories = new(2013, 5, 17);

    /// <summary>
    /// Taylor Swift - 1989 (Taylor's Version) (October 27, 2023)
    /// </summary>
    public static readonly DateTime TaylorSwift1989TV = new(2023, 10, 27);

    /// <summary>
    /// Gets all known reference dates for iteration.
    /// </summary>
    public static IEnumerable<(string AlbumName, string Artist, DateTime ReleaseDate)> GetAll()
    {
        yield return ("Kind of Blue", "Miles Davis", KindOfBlue);
        yield return ("The Dark Side of the Moon", "Pink Floyd", DarkSideOfTheMoon);
        yield return ("OK Computer", "Radiohead", OkComputer);
        yield return ("Random Access Memories", "Daft Punk", RandomAccessMemories);
        yield return ("1989 (Taylor's Version)", "Taylor Swift", TaylorSwift1989TV);
    }
}
