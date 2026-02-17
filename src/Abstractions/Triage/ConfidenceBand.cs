// <copyright file="ConfidenceBand.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

namespace Lidarr.Plugin.Common.Abstractions.Triage;

/// <summary>
/// Standardized confidence band classification for triage decisions.
/// Provides a coarse-grained view of confidence levels used by
/// recommendation triage advisors across all plugins.
/// </summary>
public enum ConfidenceBand
{
    /// <summary>Confidence score >= 0.8 — high certainty, typically auto-acceptable.</summary>
    High,

    /// <summary>Confidence score >= 0.6 and &lt; 0.8 — moderate certainty, may need review.</summary>
    Medium,

    /// <summary>Confidence score &lt; 0.6 — low certainty, typically requires manual review.</summary>
    Low
}
