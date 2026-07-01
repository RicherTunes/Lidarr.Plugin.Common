using System;
using System.Collections.Generic;

namespace Lidarr.Plugin.Common.Services.Intelligence
{
    /// <summary>
    /// The result of <see cref="SearchQuerySanitizer.Sanitize(string, SanitizerOptions)"/>.
    /// </summary>
    /// <param name="Original">
    /// NFC-normalized, control/format-deleted, whitespace-collapsed+trimmed canonical form. Never null;
    /// <see cref="string.Empty"/> when no usable signal survives. Safe as a cache/dedup key (idempotent).
    /// </param>
    /// <param name="Variants">
    /// Ordered best-first, distinct (OrdinalIgnoreCase), every element non-empty/non-whitespace. Empty
    /// only when <paramref name="HasSignal"/> is false.
    /// </param>
    /// <param name="HasSignal">True iff <paramref name="Original"/> carries a usable letter/number.</param>
    /// <param name="NeedsAlias">
    /// True when no usable alphanumeric signal survives (symbol/emoji/punctuation-only, or a single
    /// stray ASCII letter) — the caller must use an alias map / artist-only scope, never an empty or
    /// sub-2-char query.
    /// </param>
    /// <param name="RequiresArtistScope">
    /// True for 1-2 char / pure-digit / common-dictionary-word titles that must be conjoined with the
    /// artist (never issued as an unanchored title-only query).
    /// </param>
    public readonly record struct SanitizedQuery(
        string Original,
        IReadOnlyList<string> Variants,
        bool HasSignal,
        bool NeedsAlias,
        bool RequiresArtistScope);

    /// <summary>
    /// Ordered fallback tiers produced by <see cref="SearchQuerySanitizer.BuildPlan(string, string, SanitizerOptions)"/>:
    /// [combined, artist-only, album-only]. Each tier is a best-first variant list.
    /// </summary>
    public sealed record SearchPlan(IReadOnlyList<IReadOnlyList<string>> Tiers)
    {
        /// <summary>An empty plan — emitted when neither field carries signal (no API call).</summary>
        public static readonly SearchPlan Empty = new(Array.Empty<IReadOnlyList<string>>());

        /// <summary>The combined "artist album" tier (tier 0), or an empty list for an empty plan.</summary>
        public IReadOnlyList<string> Combined => Tiers.Count > 0 ? Tiers[0] : Array.Empty<string>();
    }

    /// <summary>
    /// Tuning knobs for <see cref="SearchQuerySanitizer"/>. The defaults are the cross-plugin baseline;
    /// the two crosswalk/version features are OFF by default because they can over-fold real titles and
    /// must only ever ADD ranked variants, never replace the original.
    /// </summary>
    public sealed record SanitizerOptions
    {
        /// <summary>Emit an NFD-strip-marks ASCII accent-fold variant (Beyoncé → beyonce). Default true.</summary>
        public bool FoldAccents { get; init; } = true;

        /// <summary>Expand non-decomposable special letters via the transliteration table (æ→ae, ß→ss, þ→th). Default true.</summary>
        public bool ExpandSpecialLetters { get; init; } = true;

        /// <summary>Emit a spelled-out connective variant (&amp;/+/N → "and"). Default true.</summary>
        public bool ExpandConnectives { get; init; } = true;

        /// <summary>Emit an NFKC compatibility key variant (fullwidth → ASCII, presentation ligatures). Default true.</summary>
        public bool ApplyNfkc { get; init; } = true;

        /// <summary>Emit a confusable/homoglyph-folded variant for mixed-script tokens (KoЯn → Korn). Default true.</summary>
        public bool FoldConfusables { get; init; } = true;

        /// <summary>Emit clause-stripped edition/feat/live/remaster variants (ranked below the original). Default false.</summary>
        public bool StripVersionSuffix { get; init; }

        /// <summary>Emit roman↔arabic / number-word crosswalk variants (IV↔4, Twenty↔20). Default false.</summary>
        public bool RomanArabicCrosswalk { get; init; }

        /// <summary>Maximum variant length in chars (grapheme-cluster-safe truncation) before the URL is built. Default 120.</summary>
        public int MaxLength { get; init; } = 120;

        /// <summary>Maximum variant length in space-delimited tokens. Default 12.</summary>
        public int MaxTokens { get; init; } = 12;

        /// <summary>
        /// Optional resolver consulted by <see cref="SearchQuerySanitizer.BuildPlan(string, string, SanitizerOptions)"/>
        /// when a field has no signal (symbol/emoji-only) — maps e.g. "★" → "Blackstar".
        /// </summary>
        public Func<string, string?>? AliasResolver { get; init; }

        /// <summary>The shared default options instance.</summary>
        public static SanitizerOptions Default { get; } = new();
    }
}
