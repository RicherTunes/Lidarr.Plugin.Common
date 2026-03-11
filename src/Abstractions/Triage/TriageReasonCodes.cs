// <copyright file="TriageReasonCodes.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

namespace Lidarr.Plugin.Common.Abstractions.Triage;

/// <summary>
/// Canonical reason code constants for recommendation triage decisions.
/// All plugins performing automated queue triage should use these constants
/// instead of defining local reason code strings to ensure ecosystem-wide parity.
/// </summary>
public static class TriageReasonCodes
{
    /// <summary>Item confidence score is below the configured minimum threshold.</summary>
    public const string ConfidenceBelowThreshold = "CONFIDENCE_BELOW_THRESHOLD";

    /// <summary>Item confidence score is substantially below the threshold (>= 0.15 gap).</summary>
    public const string ConfidenceFarBelowThreshold = "CONFIDENCE_FAR_BELOW_THRESHOLD";

    /// <summary>Required MusicBrainz identifiers (artist/album) are missing.</summary>
    public const string MissingRequiredMbids = "MISSING_REQUIRED_MBIDS";

    /// <summary>Duplicate-like signal detected in recommendation rationale.</summary>
    public const string DuplicateSignal = "DUPLICATE_SIGNAL";

    /// <summary>High confidence score combined with verified artist MBID (risk reduction).</summary>
    public const string HighConfidenceWithMbid = "HIGH_CONFIDENCE_WITH_MBID";

    /// <summary>All signals are consistent; no risk factors detected.</summary>
    public const string ConsistentSignals = "CONSISTENT_SIGNALS";

    /// <summary>Provider calibration adjusted the raw confidence score.</summary>
    public const string CalibrationApplied = "CALIBRATION_APPLIED";

    /// <summary>Provider is known to produce lower-quality confidence estimates.</summary>
    public const string LowCalibrationProvider = "LOW_CALIBRATION_PROVIDER";
}
