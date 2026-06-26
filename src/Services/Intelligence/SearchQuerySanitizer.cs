using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Lidarr.Plugin.Common.Services.Intelligence
{
    /// <summary>
    /// Canonical, dependency-free search-query sanitizer shared by every streaming plugin
    /// (qobuz/tidal/apple/amazon). Promotes Tidalarr's internal <c>TidalSearchTermBuilder</c>
    /// into Common and generalizes its <c>char</c>-based stripping to <see cref="Rune"/>/text-element
    /// iteration so astral letters (CJK-Ext-B, math-alphanumeric) are never deleted as symbols.
    ///
    /// <para><b>This is NOT <see cref="MetadataFieldSanitizer"/></b> (which is FILE-SYSTEM-safe and
    /// maps <c>'/'</c>→<c>'_'</c>, <c>':'</c>→<c>'-'</c> — unusable for search) and NOT an HTML encoder.
    /// The output is RAW search text; transport-encoding (percent / HTML) belongs to the request
    /// builder. Applying an HTML encoder to a search term is the exact bug that shipped
    /// ("Beyoncé" → "Beyonc&#233;") — see <see cref="ToQueryParameterValue"/> for the only
    /// transport-encoding entry point.</para>
    ///
    /// <para>It exists because a single over-specific query loses albums whose titles carry symbols
    /// the service normalizes differently — e.g. "Record n°V", whose Qobuz slug is <c>record-nv</c>
    /// (the degree sign is dropped, NOT turned into a space). Rather than one lossy transform,
    /// <see cref="Sanitize(string, SanitizerOptions)"/> emits an ordered set of variants the indexer
    /// can try in turn, and <see cref="BuildPlan(string, string, SanitizerOptions)"/> orders the
    /// combined → artist-only → album-only fallback tiers.</para>
    ///
    /// <para><b>Guarantees (asserted by the shared corpus tests):</b> never throws on any input
    /// (250 ms regex timeouts caught → safe degrade; unpaired UTF-16 surrogates scrubbed before any
    /// <c>String.Normalize</c>); never returns null; never emits an empty/whitespace variant; never
    /// emits an empty query when the input has a usable letter/digit; idempotent over
    /// <see cref="SanitizedQuery.Original"/>; deterministic / order-stable; culture-invariant casing;
    /// HTML-decode before stripping; control/zero-width DELETED not spaced; any Unicode letter is
    /// never down-mapped to a space; grapheme-cluster-safe truncation and emoji stripping.</para>
    /// </summary>
    public static class SearchQuerySanitizer
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

        private static readonly Regex AlnumSignal =
            new(@"[\p{L}\p{N}]", RegexOptions.Compiled, RegexTimeout);

        private static readonly Regex MultiWhitespace =
            new(@"\s+", RegexOptions.Compiled, RegexTimeout);

        // ' N ' / ' N' ' / '-N-' connective (Guns N' Roses, Salt-N-Pepa) → 'and'.
        private static readonly Regex NConnective =
            new(@"(?<=[\s\-])[Nn]'?(?=[\s\-])", RegexOptions.Compiled, RegexTimeout);

        // Trailing marketing suffix " - Single" / " - EP" (Apple/Amazon). The " - " anchor must
        // survive into here, so this runs against the typographically-canonicalized form.
        private static readonly Regex MarketingSuffix =
            new(@"\s-\s(Single|EP)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

        // Letters with NO (or only compatibility) NFD decomposition, plus length-changing casefolds.
        // Applied BEFORE NFD-strip-marks so the whole accent family folds uniformly.
        private static readonly Dictionary<char, string> SpecialLetters = new()
        {
            ['æ'] = "ae", ['Æ'] = "Ae", ['œ'] = "oe", ['Œ'] = "Oe",
            ['ß'] = "ss", ['ẞ'] = "SS",
            ['ø'] = "o", ['Ø'] = "O",
            ['ł'] = "l", ['Ł'] = "L",
            ['đ'] = "d", ['Đ'] = "D",
            ['ð'] = "d", ['Ð'] = "D",
            ['þ'] = "th", ['Þ'] = "Th",
            ['ħ'] = "h", ['Ħ'] = "H",
            ['ŋ'] = "ng", ['Ŋ'] = "Ng",
            ['ı'] = "i", ['İ'] = "I",
            ['ĸ'] = "k", ['ŉ'] = "n",
            ['ŧ'] = "t", ['Ŧ'] = "T",
        };

        // Visually-identical Cyrillic/Greek → Latin skeletons + the multiplication-sign confusable.
        // Folding is gated (only emitted when the result has no residual non-ASCII LETTER) so a
        // wholly-Cyrillic/Greek title is never corrupted into Latin garbage the catalog won't match.
        private static readonly Dictionary<int, string> Homoglyphs = new()
        {
            // Cyrillic uppercase
            [0x0410] = "A", [0x0412] = "B", [0x0415] = "E", [0x041A] = "K", [0x041C] = "M",
            [0x041D] = "H", [0x041E] = "O", [0x0420] = "P", [0x0421] = "C", [0x0422] = "T",
            [0x0423] = "Y", [0x0425] = "X", [0x0405] = "S", [0x0406] = "I", [0x0408] = "J",
            [0x042F] = "R", [0x0414] = "D",
            // Cyrillic lowercase (only the unambiguous look-alikes)
            [0x0430] = "a", [0x0435] = "e", [0x043E] = "o", [0x0441] = "c", [0x0440] = "p",
            [0x0443] = "y", [0x0445] = "x", [0x0455] = "s", [0x0456] = "i", [0x0458] = "j",
            [0x044F] = "r", [0x0434] = "d", [0x0442] = "t",
            // Greek uppercase
            [0x0391] = "A", [0x0392] = "B", [0x0395] = "E", [0x0396] = "Z", [0x0397] = "H",
            [0x0399] = "I", [0x039A] = "K", [0x039C] = "M", [0x039D] = "N", [0x039F] = "O",
            [0x03A1] = "P", [0x03A4] = "T", [0x03A5] = "Y", [0x03A7] = "X",
            // Sigma stylized as 'E' (Bon Iver "CRΣΣKS" → "CREEKS")
            [0x03A3] = "E",
            // Multiplication sign is the ASCII 'x' confusable (Ed Sheeran "×")
            [0x00D7] = "x",
        };

        // Small-number word ↔ arabic crosswalk (opt-in). Only multi-letter / unambiguous entries.
        private static readonly Dictionary<string, string> NumberWords = new(StringComparer.OrdinalIgnoreCase)
        {
            ["zero"] = "0", ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4",
            ["five"] = "5", ["six"] = "6", ["seven"] = "7", ["eight"] = "8", ["nine"] = "9",
            ["ten"] = "10", ["eleven"] = "11", ["twelve"] = "12", ["thirteen"] = "13",
            ["fourteen"] = "14", ["fifteen"] = "15", ["sixteen"] = "16", ["seventeen"] = "17",
            ["eighteen"] = "18", ["nineteen"] = "19", ["twenty"] = "20", ["thirty"] = "30",
            ["forty"] = "40", ["fifty"] = "50", ["hundred"] = "100",
        };

        // Roman numerals — multi-letter only; single ambiguous letters (I V X L C D M) are NEVER
        // crosswalked (Ed Sheeran "X" is not "10"; many "I"/"II" albums).
        private static readonly Dictionary<string, string> RomanNumerals = new(StringComparer.OrdinalIgnoreCase)
        {
            ["II"] = "2", ["III"] = "3", ["IV"] = "4", ["VI"] = "6", ["VII"] = "7", ["VIII"] = "8",
            ["IX"] = "9", ["XI"] = "11", ["XII"] = "12", ["XIII"] = "13", ["XIV"] = "14", ["XV"] = "15",
            ["XX"] = "20",
        };

        // Titles that collapse to a single ultra-common dictionary word flood the catalog and must be
        // artist-scoped (the band "fun." vs the word "fun").
        private static readonly HashSet<string> AmbiguousDictionaryWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "fun", "love", "the", "one", "time", "life", "yes", "no", "hits", "gold",
            "best", "music", "songs", "up", "ok", "go", "now", "us", "war", "home",
        };

        // Clause keywords that are genuine release discriminators — kept in the promo-stripped
        // primary, only dropped to reach the deepest bare-title fallback.
        private static readonly string[] LoadBearingClause =
        {
            "remix", "taylor's version", "taylors version", "mono", "stereo",
            "instrumental", "minute version", "deluxe remix",
        };

        // Clause keywords safe to drop for a studio-seeking primary query (i18n edition/feat/live).
        private static readonly string[] DroppableClause =
        {
            "deluxe", "expanded", "special", "bonus", "edition", "anniversary",
            "remaster", "remastered", "radio edit", "single version", "album version",
            "original mix", "live", "unplugged", "acoustic", "from the vault",
            "feat", "feat.", "ft", "ft.", "featuring", "with", "explicit", "clean",
            // localized edition / feat markers
            "édition", "edicion", "edición", "deluxe edition", "デラックス", "エディション",
            "avec", "con",
        };

        /// <summary>
        /// Sanitizes a single raw search term into its canonical <see cref="SanitizedQuery.Original"/>
        /// plus an ordered, distinct ladder of <see cref="SanitizedQuery.Variants"/>.
        /// </summary>
        public static SanitizedQuery Sanitize(string? raw, SanitizerOptions? options = null)
        {
            options ??= SanitizerOptions.Default;

            var canonical = Canonicalize(raw);

            var hasAlnum = SafeIsMatch(AlnumSignal, canonical);

            // No-signal rescue: a bare confusable that is really a letter/digit (e.g. "×" → "x")
            // should yield signal rather than route to alias.
            if (!hasAlnum && options.FoldConfusables)
            {
                var rescue = FoldConfusables(canonical);
                if (rescue != null && SafeIsMatch(AlnumSignal, rescue))
                {
                    canonical = CollapseWhitespace(rescue);
                    hasAlnum = true;
                }
            }

            var glued = StripSymbols(canonical);
            ComputeSignalShape(glued, out var longestToken, out var isPureNumber, out var allAscii, out var singleToken, out var soleToken);

            // A single residual ASCII-Latin letter (or zero letters) is not a usable query — route to
            // alias. A single ideographic/syllabic char (훗) or a bare number (7) IS usable.
            var singleLatinResidue = longestToken < 2 && !isPureNumber && allAscii;
            var needsAlias = !hasAlnum || singleLatinResidue;
            var hasSignal = hasAlnum && !needsAlias;

            var requiresArtistScope = hasSignal &&
                (longestToken <= 2
                 || isPureNumber
                 || (singleToken && soleToken != null && AmbiguousDictionaryWords.Contains(soleToken)));

            if (!hasSignal)
            {
                return new SanitizedQuery(string.Empty, Array.Empty<string>(), false, true, false);
            }

            var variants = GenerateVariants(canonical, options);
            return new SanitizedQuery(canonical, variants, true, false, requiresArtistScope);
        }

        /// <summary>
        /// Percent-encodes a variant for the query COMPONENT of a URL via <see cref="Uri.EscapeDataString"/>,
        /// so URL-significant glyphs (%, +, /, #, ?, =, &amp;) never reach the wire uninterpreted and the
        /// value round-trips exactly. Never use the result as a path segment.
        /// </summary>
        public static string ToQueryParameterValue(string variant)
            => Uri.EscapeDataString(variant ?? string.Empty);

        /// <summary>
        /// Builds the ordered fallback tiers [combined, artist-only, album-only]. Combined is always
        /// tier 0; a non-truncated artist-only tier is present whenever both fields carry signal; a
        /// title that <see cref="SanitizedQuery.RequiresArtistScope"/> never becomes an unanchored
        /// album-only tier; both-fields-empty yields an empty plan (no API call).
        /// </summary>
        public static SearchPlan BuildPlan(string? artist, string? album, SanitizerOptions? options = null)
        {
            options ??= SanitizerOptions.Default;

            var aQ = ResolveField(artist, options);
            var bQ = ResolveField(album, options);

            var tiers = new List<IReadOnlyList<string>>();

            var combined = BuildCombinedTier(aQ, bQ, options);
            if (combined.Count > 0)
            {
                tiers.Add(combined);
            }

            // Artist-only / album-only only add value when BOTH parts carry signal — otherwise the
            // combined tier already IS the artist-only (or album-only) query.
            if (aQ.HasSignal && bQ.HasSignal)
            {
                if (aQ.Variants.Count > 0)
                {
                    tiers.Add(aQ.Variants); // FULL artist, never truncated (the shipped Bleu-Jeans bug)
                }

                // A RequiresArtistScope album (e.g. "7") must never become an unanchored album-only tier.
                if (!bQ.RequiresArtistScope && bQ.Variants.Count > 0)
                {
                    tiers.Add(bQ.Variants);
                }
            }

            return tiers.Count == 0 ? SearchPlan.Empty : new SearchPlan(tiers);
        }

        /// <summary>
        /// Casefold + connective/quote/confusable-insensitive equality for ranking — "JAY-Z" ≡ "jay-z",
        /// "Simon &amp; Garfunkel" ≡ "Simon and Garfunkel". Never mutates its inputs.
        /// </summary>
        public static bool AreEquivalent(string? a, string? b)
            => string.Equals(EquivalenceKey(a), EquivalenceKey(b), StringComparison.Ordinal);

        // ----- field resolution / plan assembly -----

        private static SanitizedQuery ResolveField(string? raw, SanitizerOptions options)
        {
            var q = Sanitize(raw, options);
            if (!q.HasSignal && options.AliasResolver != null && !string.IsNullOrWhiteSpace(raw))
            {
                var alias = options.AliasResolver(raw.Trim());
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    var resolved = Sanitize(alias, options);
                    if (resolved.HasSignal)
                    {
                        return resolved;
                    }
                }
            }

            return q;
        }

        private static IReadOnlyList<string> BuildCombinedTier(SanitizedQuery artist, SanitizedQuery album, SanitizerOptions options)
        {
            var parts = new List<string>(2);
            if (artist.HasSignal)
            {
                parts.Add(artist.Original);
            }

            if (album.HasSignal)
            {
                parts.Add(album.Original);
            }

            if (parts.Count == 0)
            {
                return Array.Empty<string>();
            }

            // Eponymous de-dup: "Beyoncé Beyoncé" collapses to "Beyoncé" so the self-titled case
            // isn't an over-specific double-name query.
            string combinedText;
            if (parts.Count == 2 && AreEquivalent(parts[0], parts[1]))
            {
                combinedText = parts[0];
            }
            else
            {
                combinedText = string.Join(" ", parts);
            }

            return Sanitize(combinedText, options).Variants;
        }

        // ----- variant ladder -----

        private static IReadOnlyList<string> GenerateVariants(string canonical, SanitizerOptions options)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void TryAdd(string? candidate)
            {
                if (string.IsNullOrEmpty(candidate))
                {
                    return;
                }

                var normalized = TruncateToBudget(CollapseWhitespace(candidate), options);
                if (normalized.Length > 0 && !string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
                {
                    result.Add(normalized);
                }
            }

            // Each "seed" is expanded into BOTH removal modes — the glued form (symbol dropped, tokens
            // stay adjacent: "n°V" → "nV") AND the spaced form (true separators survive: "AC/DC" → "AC DC").
            void AddSeed(string? seed)
            {
                if (string.IsNullOrEmpty(seed))
                {
                    return;
                }

                TryAdd(seed);
                TryAdd(StripSymbols(seed));
                TryAdd(SymbolsToSpace(seed));
            }

            // 1. canonical (Original) + both removal modes.
            AddSeed(canonical);

            // 2. promo-stripped editions ranked just below the literal title (opt-in).
            string? bareTitleSeed = null;
            if (options.StripVersionSuffix)
            {
                var (promo, bare) = StripVersionClauses(canonical);
                if (promo != null)
                {
                    AddSeed(promo);
                }

                bareTitleSeed = bare;
            }

            // 3. leetspeak letter substitution ($ → s): "Joey Bada$$" → "Joey Badass".
            var dollar = SubstituteDollarS(canonical);
            if (!string.Equals(dollar, canonical, StringComparison.Ordinal))
            {
                AddSeed(dollar);
            }

            // 4. spelled-out connective ('and'): "Simon & Garfunkel" → "Simon and Garfunkel".
            if (options.ExpandConnectives)
            {
                AddSeed(ExpandConnectives(canonical));
            }

            // 5. ASCII accent fold (+ special-letter table).
            if (options.FoldAccents)
            {
                AddSeed(FoldToAscii(canonical, options.ExpandSpecialLetters));
            }

            // 6. confusable / homoglyph skeleton (mixed-script tokens only).
            if (options.FoldConfusables)
            {
                AddSeed(FoldConfusables(canonical));
            }

            // 7. NFKC compat key (fullwidth → ASCII, presentation ligatures, math-alphanumeric letters).
            if (options.ApplyNfkc)
            {
                var nfkc = SafeNormalize(canonical, NormalizationForm.FormKC);
                if (!string.Equals(nfkc, canonical, StringComparison.Ordinal))
                {
                    AddSeed(nfkc);
                }
            }

            // 8. roman ↔ arabic / number-word crosswalk (opt-in, never replaces the original).
            if (options.RomanArabicCrosswalk)
            {
                AddSeed(NumeralCrosswalk(canonical));
            }

            // 9. deepest fallback: the bare title with every clause removed (ranked last).
            if (bareTitleSeed != null)
            {
                AddSeed(bareTitleSeed);
            }

            return result;
        }

        // ----- canonical form -----

        private static string Canonicalize(string? raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return string.Empty;
            }

            // Scrub unpaired UTF-16 surrogates FIRST — String.Normalize throws on invalid UTF-16.
            var scrubbed = ScrubSurrogates(raw);

            // HTML/XML entity decode before any character-class handling so "&amp;" → "&" (and a bare
            // "&" in R&B/AT&T is left untouched).
            scrubbed = SafeHtmlDecode(scrubbed);

            // Canonical composition (Apple/macOS deliver NFD; unify so cache keys don't diverge).
            scrubbed = SafeNormalize(scrubbed, NormalizationForm.FormC);

            // Delete control (Cc) + format (Cf) chars and emoji presentation marks. ZWJ/ZWNJ are kept
            // (semantic in Indic/Persian); whole emoji graphemes are removed in the next pass.
            scrubbed = DeleteControlAndFormat(scrubbed);
            scrubbed = StripEmoji(scrubbed);

            // Typographic → ASCII (quotes, dashes, ellipsis, apostrophe family, fraction slash).
            scrubbed = CanonicalizeTypographic(scrubbed);

            return CollapseWhitespace(scrubbed);
        }

        private static string ScrubSurrogates(string value)
        {
            var sb = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    {
                        sb.Append(c);
                        sb.Append(value[i + 1]);
                        i++;
                    }

                    // else: drop the unpaired high surrogate.
                }
                else if (char.IsLowSurrogate(c))
                {
                    // drop the unpaired low surrogate.
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private static string DeleteControlAndFormat(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var rune in value.EnumerateRunes())
            {
                var v = rune.Value;

                // Semantic joiners stay (Indic/Persian ZWNJ/ZWJ; emoji ZWJ is consumed in StripEmoji).
                if (v == 0x200C || v == 0x200D)
                {
                    sb.Append(rune.ToString());
                    continue;
                }

                // Variation selectors + combining enclosing keycap are emoji presentation noise.
                if ((v >= 0xFE00 && v <= 0xFE0F) || (v >= 0xE0100 && v <= 0xE01EF) || v == 0x20E3)
                {
                    continue;
                }

                var cat = Rune.GetUnicodeCategory(rune);
                if (cat == UnicodeCategory.Control || cat == UnicodeCategory.Format)
                {
                    continue;
                }

                sb.Append(rune.ToString());
            }

            return sb.ToString();
        }

        private static string StripEmoji(string value)
        {
            var sb = new StringBuilder(value.Length);
            var runes = value.EnumerateRunes().ToList();
            for (var i = 0; i < runes.Count; i++)
            {
                if (!IsEmojiRune(runes[i]))
                {
                    sb.Append(runes[i].ToString());
                    continue;
                }

                // Consume the whole emoji ZWJ sequence (emoji (ZWJ emoji)*).
                while (i + 1 < runes.Count && runes[i + 1].Value == 0x200D && i + 2 < runes.Count && IsEmojiRune(runes[i + 2]))
                {
                    i += 2;
                }
            }

            return sb.ToString();
        }

        private static bool IsEmojiRune(Rune rune)
        {
            var v = rune.Value;
            return (v >= 0x1F000 && v <= 0x1FAFF)
                || (v >= 0x2600 && v <= 0x27BF)   // misc symbols + dingbats (incl. ★ U+2605, ✝ U+271D)
                || (v >= 0x2B00 && v <= 0x2BFF)   // misc symbols and arrows (incl. ⭐ U+2B50)
                || (v >= 0x1F1E6 && v <= 0x1F1FF) // regional indicators (flags)
                || v == 0x2764;                   // heavy black heart
        }

        private static string CanonicalizeTypographic(string value)
        {
            var sb = new StringBuilder(value.Length + 4);
            foreach (var rune in value.EnumerateRunes())
            {
                switch (rune.Value)
                {
                    // Apostrophe family → U+0027 (incl. Hawaiian okina, primes, accents-as-quote).
                    case 0x2018:
                    case 0x2019:
                    case 0x201B:
                    case 0x02BB:
                    case 0x02BC:
                    case 0x02B9:
                    case 0x0060:
                    case 0x00B4:
                    case 0x2032:
                    case 0x05F3:
                        sb.Append('\'');
                        break;

                    // Double-quote family → U+0022.
                    case 0x201C:
                    case 0x201D:
                    case 0x201E:
                    case 0x201F:
                    case 0x2033:
                    case 0x2036:
                        sb.Append('"');
                        break;

                    // Dash / minus family → U+002D (NOT U+30FC choonpu — that is a CJK letter).
                    case 0x2010:
                    case 0x2011:
                    case 0x2012:
                    case 0x2013:
                    case 0x2014:
                    case 0x2015:
                    case 0x2043:
                    case 0x2212:
                        sb.Append('-');
                        break;

                    // Ellipsis → "..."
                    case 0x2026:
                        sb.Append("...");
                        break;

                    // Fraction slash (NFKC injects this for ½) → "/" so it is treated as a separator.
                    case 0x2044:
                        sb.Append('/');
                        break;

                    default:
                        sb.Append(rune.ToString());
                        break;
                }
            }

            return sb.ToString();
        }

        // ----- token / signal shape -----

        private static void ComputeSignalShape(string glued, out int longestToken, out bool isPureNumber, out bool allAscii, out bool singleToken, out string? soleToken)
        {
            longestToken = 0;
            allAscii = true;
            var anyAlnum = false;
            var anyNonDigit = false;

            var tokens = glued.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            singleToken = tokens.Length == 1;
            soleToken = singleToken ? tokens[0] : null;

            foreach (var token in tokens)
            {
                var len = 0;
                foreach (var rune in token.EnumerateRunes())
                {
                    len++;
                    if (Rune.IsLetterOrDigit(rune))
                    {
                        anyAlnum = true;
                        if (!Rune.IsDigit(rune))
                        {
                            anyNonDigit = true;
                        }

                        if (rune.Value > 0x7F)
                        {
                            allAscii = false;
                        }
                    }
                }

                if (len > longestToken)
                {
                    longestToken = len;
                }
            }

            isPureNumber = anyAlnum && !anyNonDigit;
        }

        // ----- rune-aware symbol passes -----

        private static string StripSymbols(string value) => MapNonWord(value, toSpace: false);

        private static string SymbolsToSpace(string value) => MapNonWord(value, toSpace: true);

        private static string MapNonWord(string value, bool toSpace)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var rune in value.EnumerateRunes())
            {
                if (IsKeptRune(rune))
                {
                    sb.Append(rune.ToString());
                }
                else if (toSpace)
                {
                    sb.Append(' ');
                }

                // else: drop (glue neighbours).
            }

            return CollapseWhitespace(sb.ToString());
        }

        private static bool IsKeptRune(Rune rune)
        {
            if (Rune.IsWhiteSpace(rune))
            {
                return true;
            }

            if (Rune.IsLetterOrDigit(rune))
            {
                return true;
            }

            // Keep combining marks so Devanagari/Thai/Arabic dependent vowels & tone marks survive.
            var cat = Rune.GetUnicodeCategory(rune);
            return cat == UnicodeCategory.NonSpacingMark
                || cat == UnicodeCategory.SpacingCombiningMark;
        }

        // ----- folds -----

        private static string FoldToAscii(string value, bool expandSpecialLetters)
        {
            var sb = new StringBuilder(value.Length + 4);
            foreach (var c in value)
            {
                if (expandSpecialLetters && SpecialLetters.TryGetValue(c, out var rep))
                {
                    sb.Append(rep);
                }
                else
                {
                    sb.Append(c);
                }
            }

            var decomposed = SafeNormalize(sb.ToString(), NormalizationForm.FormD);
            var stripped = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    stripped.Append(c);
                }
            }

            return SafeNormalize(stripped.ToString(), NormalizationForm.FormC);
        }

        private static string? FoldConfusables(string value)
        {
            var sb = new StringBuilder(value.Length);
            var anyMapped = false;
            foreach (var rune in value.EnumerateRunes())
            {
                if (Homoglyphs.TryGetValue(rune.Value, out var rep))
                {
                    sb.Append(rep);
                    anyMapped = true;
                }
                else
                {
                    sb.Append(rune.ToString());
                }
            }

            if (!anyMapped)
            {
                return null;
            }

            var folded = sb.ToString();

            // Gate: discard if any LETTER survives non-ASCII — that means this was a real non-Latin
            // word, not a homoglyph spoof. Symbols/punctuation are allowed to remain.
            foreach (var rune in folded.EnumerateRunes())
            {
                if (Rune.IsLetter(rune) && rune.Value > 0x7F)
                {
                    return null;
                }
            }

            return folded;
        }

        private static string SubstituteDollarS(string value) => value.Replace('$', 's');

        private static string ExpandConnectives(string value)
        {
            var s = value.Replace("&", " and ");
            s = SafeReplace(NConnective, s, "and");
            s = s.Replace(" + ", " and ");
            return CollapseWhitespace(s);
        }

        private static string NumeralCrosswalk(string value)
        {
            var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < tokens.Length; i++)
            {
                var bare = StripSymbols(tokens[i]);
                if (RomanNumerals.TryGetValue(bare, out var arabic) || NumberWords.TryGetValue(bare, out arabic))
                {
                    tokens[i] = arabic;
                }
            }

            return CollapseWhitespace(string.Join(" ", tokens));
        }

        // ----- version / clause stripping -----

        private static (string? Promo, string? Bare) StripVersionClauses(string canonical)
        {
            var groups = ParseBracketGroups(canonical);
            if (groups.Count == 0)
            {
                var trimmed = SafeReplace(MarketingSuffix, canonical, string.Empty);
                return string.Equals(trimmed, canonical, StringComparison.Ordinal)
                    ? (null, null)
                    : (CollapseWhitespace(trimmed), CollapseWhitespace(trimmed));
            }

            // Promo-stripped: drop droppable non-leading groups; keep load-bearing / unknown / leading.
            // Bare: also drop load-bearing trailing groups (deepest fallback).
            var promo = RemoveGroups(canonical, groups, dropLoadBearing: false);
            var bare = RemoveGroups(canonical, groups, dropLoadBearing: true);

            promo = CollapseWhitespace(SafeReplace(MarketingSuffix, promo, string.Empty));
            bare = CollapseWhitespace(SafeReplace(MarketingSuffix, bare, string.Empty));

            string? promoOut = string.Equals(promo, canonical, StringComparison.Ordinal) ? null : promo;
            string? bareOut = string.IsNullOrWhiteSpace(bare) || string.Equals(bare, canonical, StringComparison.Ordinal) ? null : bare;
            return (promoOut, bareOut);
        }

        private readonly record struct BracketGroup(int Start, int End, string Inner, bool IsLeading);

        private static List<BracketGroup> ParseBracketGroups(string text)
        {
            var groups = new List<BracketGroup>();
            var openers = new Dictionary<char, char> { ['('] = ')', ['['] = ']', ['{'] = '}' };
            var firstNonWs = 0;
            while (firstNonWs < text.Length && char.IsWhiteSpace(text[firstNonWs]))
            {
                firstNonWs++;
            }

            for (var i = 0; i < text.Length; i++)
            {
                if (!openers.TryGetValue(text[i], out var close))
                {
                    continue;
                }

                var depth = 1;
                var j = i + 1;
                for (; j < text.Length && depth > 0; j++)
                {
                    if (text[j] == text[i])
                    {
                        depth++;
                    }
                    else if (text[j] == close)
                    {
                        depth--;
                    }
                }

                // Balance-aware: a closer extends to end-of-string when unbalanced.
                var end = depth == 0 ? j : text.Length;
                var inner = text.Substring(i + 1, Math.Max(0, end - i - 2 + (depth == 0 ? 0 : 1)));
                groups.Add(new BracketGroup(i, end, inner, i == firstNonWs));
                i = end - 1;
            }

            return groups;
        }

        private static string RemoveGroups(string text, List<BracketGroup> groups, bool dropLoadBearing)
        {
            var sb = new StringBuilder(text.Length);
            var cursor = 0;
            foreach (var g in groups)
            {
                var loadBearing = ContainsAny(g.Inner, LoadBearingClause);
                var droppable = ContainsAny(g.Inner, DroppableClause);

                // Preserve a leading group or one with no recognized clause words (title-integral).
                var shouldDrop = !g.IsLeading && (droppable && !loadBearing || (dropLoadBearing && loadBearing));
                if (!shouldDrop)
                {
                    continue;
                }

                sb.Append(text, cursor, g.Start - cursor);
                cursor = g.End;
            }

            sb.Append(text, cursor, text.Length - cursor);
            return sb.ToString();
        }

        private static bool ContainsAny(string haystack, string[] needles)
        {
            foreach (var n in needles)
            {
                if (haystack.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        // ----- equivalence -----

        private static string EquivalenceKey(string? value)
        {
            var canonical = Canonicalize(value);
            var folded = FoldToAscii(ExpandConnectives(canonical), expandSpecialLetters: true);
            var rescued = FoldConfusables(folded);
            var glued = StripSymbols(rescued ?? folded);
            return SafeReplace(MultiWhitespace, glued, string.Empty).ToLowerInvariant();
        }

        // ----- length budget -----

        private static string TruncateToBudget(string text, SanitizerOptions options)
        {
            if (text.Length == 0)
            {
                return text;
            }

            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (options.MaxTokens > 0 && tokens.Length > options.MaxTokens)
            {
                text = string.Join(' ', tokens.Take(options.MaxTokens));
            }

            if (options.MaxLength <= 0 || text.Length <= options.MaxLength)
            {
                return text;
            }

            // Grapheme-cluster-safe cut (never split a surrogate pair or combining sequence).
            var sb = new StringBuilder(options.MaxLength);
            var enumerator = StringInfo.GetTextElementEnumerator(text);
            while (enumerator.MoveNext())
            {
                var element = (string)enumerator.Current;
                if (sb.Length + element.Length > options.MaxLength)
                {
                    break;
                }

                sb.Append(element);
            }

            var cut = sb.ToString();

            // Avoid ending mid-word: trim back to the last whole token when we cut inside one.
            if (cut.Length < text.Length && text[cut.Length] != ' ')
            {
                var lastSpace = cut.LastIndexOf(' ');
                if (lastSpace > 0)
                {
                    cut = cut.Substring(0, lastSpace);
                }
            }

            return CollapseWhitespace(cut);
        }

        // ----- safe primitives -----

        private static string CollapseWhitespace(string? value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : SafeReplace(MultiWhitespace, value, " ").Trim();

        private static bool SafeIsMatch(Regex regex, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            try
            {
                return regex.IsMatch(value);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        private static string SafeReplace(Regex regex, string value, string replacement)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value ?? string.Empty;
            }

            try
            {
                return regex.Replace(value, replacement);
            }
            catch (RegexMatchTimeoutException)
            {
                return value;
            }
        }

        private static string SafeNormalize(string value, NormalizationForm form)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value ?? string.Empty;
            }

            try
            {
                return value.IsNormalized(form) ? value : value.Normalize(form);
            }
            catch (ArgumentException)
            {
                // Invalid UTF-16 that slipped past the surrogate scrub — degrade safely.
                return value;
            }
        }

        private static string SafeHtmlDecode(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('&') < 0)
            {
                return value;
            }

            try
            {
                return WebUtility.HtmlDecode(value);
            }
            catch (Exception)
            {
                return value;
            }
        }
    }
}
