using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using FsCheck;
using FsCheck.Xunit;
using Lidarr.Plugin.Common.Services.Intelligence;
using Lidarr.Plugin.Common.TestKit.Data;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Intelligence;

/// <summary>
/// Executable behavior contracts for <see cref="SearchQuerySanitizer"/>. The shared tricky-character
/// corpus carries the human-readable invariant; the assertions live here. Each behavior subscribes to
/// its corpus slice (via the corpus categories) or pins the exact examples from the contract.
/// </summary>
public sealed class SearchQuerySanitizerTests
{
    private static readonly SanitizerOptions WithVersionStripping = new() { StripVersionSuffix = true };
    private static readonly SanitizerOptions WithCrosswalk = new() { RomanArabicCrosswalk = true };

    private static SanitizedQuery S(string? raw, SanitizerOptions? options = null) =>
        SearchQuerySanitizer.Sanitize(raw, options);

    private static IReadOnlyList<string> V(string? raw, SanitizerOptions? options = null) =>
        SearchQuerySanitizer.Sanitize(raw, options).Variants;

    private static bool ContainsCI(IEnumerable<string> variants, string value) =>
        variants.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsSubstringCI(IEnumerable<string> variants, string sub) =>
        variants.Any(v => v.Contains(sub, StringComparison.OrdinalIgnoreCase));

    // ===== Musical accidentals (U+266D ♭ / U+266E ♮ / U+266F ♯) must FOLD, not be stripped =====
    // Regression: IsEmojiRune swallowed the whole 0x2600..0x27BF block, deleting ♭/♮/♯ so
    // "Sonata in F♯ Minor" became "Sonata in F Minor" — losing the musical key (huge in
    // classical/jazz catalogs). ♯→'#', ♭→'b', ♮ dropped (it carries no catalog spelling).

    [Theory]
    [InlineData("Sonata in F♯ Minor", "Sonata in F# Minor")] // ♯ → #
    [InlineData("B♭ Major", "Bb Major")]                      // ♭ → b
    [InlineData("Prelude in C♯", "Prelude in C#")]            // ♯ survives next to a real token
    [InlineData("Symphony in E♭", "Symphony in Eb")]          // ♭ → b mid-phrase
    public void MusicalAccidentals_areFoldedNotStripped(string raw, string expected)
    {
        var variants = V(raw);
        Assert.True(ContainsCI(variants, expected),
            $"expected a folded variant '{expected}' for '{raw}'; got: {string.Join(" | ", variants)}");
    }

    [Fact]
    public void MusicalSharp_isNotDeletedDownToBareLetter()
    {
        // The bug produced "F Minor"; assert the sharp survives in some form.
        Assert.True(ContainsSubstringCI(V("F♯ Minor"), "F#"),
            "the musical sharp must fold to '#', not be deleted");
    }

    // ===== B1: NeverThrows_OnAnyInput (Sanitize + BuildPlan over the whole corpus + null) =====

    public static IEnumerable<object[]> AllCases => SearchQueryCorpus.AllCases;

    [Theory]
    [MemberData(nameof(AllCases))]
    public void Should_never_throw_on_any_corpus_input(SearchQueryCase c)
    {
        Assert.Null(Record.Exception(() => SearchQuerySanitizer.Sanitize(c.Raw)));
        Assert.Null(Record.Exception(() => SearchQuerySanitizer.BuildPlan(c.Raw, c.Raw)));
        Assert.NotNull(S(c.Raw).Variants);
    }

    [Fact]
    public void Should_never_throw_on_null()
    {
        Assert.Null(Record.Exception(() => SearchQuerySanitizer.Sanitize(null)));
        Assert.Null(Record.Exception(() => SearchQuerySanitizer.BuildPlan(null, null)));
        Assert.False(S(null).HasSignal);
    }

    // ===== B2: NeverReturnsEmpty_WhenInputHasLetterOrDigit =====

    public static IEnumerable<object[]> SignalCorpus => SearchQueryCorpus.CasesIn(
        "all-numeric-name", "bare-digit-ambiguity", "cyrillic", "korean-hangul-transliteration",
        "single-syllable-hangul", "trailing-period-dictionary-word");

    [Theory]
    [MemberData(nameof(SignalCorpus))]
    public void Should_not_return_empty_when_input_has_letter_or_digit(SearchQueryCase c)
    {
        var nfc = c.Raw.Normalize(NormalizationForm.FormC);
        if (!Regex.IsMatch(nfc, @"[\p{L}\p{N}]"))
        {
            return;
        }

        var variants = V(c.Raw);
        Assert.NotEmpty(variants);
        Assert.All(variants, v => Assert.False(string.IsNullOrWhiteSpace(v)));
    }

    // ===== B3: SymbolRemovedAdjacentVariant_KeepsTokensGlued =====

    [Theory]
    [InlineData("Record n°V", "Record nV")]
    [InlineData("3OH!3", "3OH3")]
    [InlineData("will.i.am", "william")]
    [InlineData("Joey Bada$$", "Joey Badass")]
    public void Should_emit_symbol_removed_adjacent_glued_variant(string raw, string glued)
    {
        Assert.True(ContainsCI(V(raw), glued), $"'{raw}' variants missing glued '{glued}': [{string.Join(" | ", V(raw))}]");
    }

    // ===== B4: SpacedSeparatorVariant_ForTrueSeparators =====

    [Theory]
    [InlineData("AC/DC", "AC DC")]
    [InlineData("Either/Or", "Either Or")]
    [InlineData("Panic! at the Disco", "Panic at the Disco")]
    public void Should_emit_symbols_to_space_variant(string raw, string spaced)
    {
        var variants = V(raw);
        Assert.True(ContainsCI(variants, spaced));
        Assert.All(variants, v => Assert.DoesNotContain("  ", v)); // no double spaces
    }

    // ===== B5: BothRemovalModes_AlwaysCoexist =====

    [Theory]
    [InlineData("Ke$ha", "Keha", "Ke ha")]
    [InlineData("P!nk", "Pnk", "P nk")]
    public void Should_emit_both_removal_modes(string raw, string glued, string spaced)
    {
        var variants = V(raw);
        Assert.True(ContainsCI(variants, glued), $"missing glued '{glued}'");
        Assert.True(ContainsCI(variants, spaced), $"missing spaced '{spaced}'");
    }

    // ===== B6: AccentPreserved_PlusAsciiFoldVariant =====

    [Theory]
    [InlineData("Beyoncé", "beyonce")]
    [InlineData("Björk", "bjork")]
    [InlineData("Mylène Farmer", "mylene farmer")]
    [InlineData("Édith Piaf", "edith piaf")]
    [InlineData("Motörhead", "motorhead")]
    [InlineData("Tiësto", "tiesto")]
    [InlineData("Antonín Dvořák", "antonin dvorak")]
    public void Should_preserve_accent_and_emit_ascii_fold(string raw, string folded)
    {
        var variants = V(raw);
        Assert.True(ContainsCI(variants, raw), $"original '{raw}' not preserved");
        Assert.True(ContainsCI(variants, folded), $"fold '{folded}' missing: [{string.Join(" | ", variants)}]");
        var foldVariant = variants.First(v => string.Equals(v, folded, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(TokenCount(raw), TokenCount(foldVariant));
    }

    // ===== B7: NonDecomposableSpecialLetters_ExpandedViaTable =====

    [Theory]
    [InlineData("Ænima", "aenima")]
    [InlineData("Cœur de pirate", "coeur de pirate")]
    [InlineData("Großstadtgeflüster", "grossstadtgefluster")]
    [InlineData("MØ", "mo")]
    [InlineData("Dawid Podsiadło", "dawid podsiadlo")]
    [InlineData("Þeyr", "theyr")]
    [InlineData("Sigurður", "sigurdur")]
    public void Should_expand_non_decomposable_special_letters(string raw, string folded)
    {
        var variants = V(raw);
        Assert.True(ContainsCI(variants, folded), $"'{raw}' missing fold '{folded}': [{string.Join(" | ", variants)}]");
        Assert.DoesNotContain(folded, c => c > 0x7F); // no residual non-ASCII
    }

    // ===== B8: InvariantCasing_NoTurkishILeak =====

    [Fact]
    public void Should_use_invariant_casing_under_turkish_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("tr-TR");

            Assert.True(ContainsCI(V("Sıla"), "sila"));

            var istanbul = V("İstanbul'u Dinliyorum");
            Assert.Contains(istanbul, v => v.ToLowerInvariant().StartsWith("istanbul", StringComparison.Ordinal)
                                           && !v.Contains('\u0307'));

            Assert.True(ContainsSubstringCI(V("Rammstein - Weißes Fleisch"), "weisses"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    // ===== B9: NfcNormalization_NfdAndNfcInputsAgree =====

    [Theory]
    [InlineData("Sinéad O'Connor")]
    [InlineData("Björk - Jóga")]
    [InlineData("방탄소년단")]
    public void Should_agree_for_nfc_and_nfd_inputs(string name)
    {
        var nfc = name.Normalize(NormalizationForm.FormC);
        var nfd = name.Normalize(NormalizationForm.FormD);

        Assert.Equal(S(nfc).Original, S(nfd).Original);
        Assert.Equal(S(nfc).Variants, S(nfd).Variants);
    }

    // ===== B10: ControlAndZeroWidthChars_DeletedNotSpaced =====

    [Fact]
    public void Should_delete_control_and_zero_width_chars()
    {
        Assert.Equal(S("Beyoncé").Original, S("Be​yoncé").Original);
        Assert.Equal("Radiohead", S("﻿Radiohead").Original);
        Assert.Equal("Radiohead", S("Radio﻿head").Original);
        Assert.Equal("Kind of Blue", S("Kind of Blue\0").Original);
    }

    // ===== B11: WhitespaceNormalized_CollapsedTrimmedIdempotent =====

    [Fact]
    public void Should_normalize_collapse_and_trim_whitespace()
    {
        Assert.Equal("OK Computer", S("  OK Computer  ").Original);
        Assert.Equal("Pink Floyd The Wall Disc 1", S("Pink Floyd\tThe Wall\nDisc 1").Original);
        Assert.DoesNotContain(' ', S("Symphonie n° 5").Original);
    }

    [Theory]
    [MemberData(nameof(AllCases))]
    public void Should_be_idempotent_over_original(SearchQueryCase c)
    {
        var once = S(c.Raw).Original;
        Assert.Equal(once, S(once).Original);
    }

    // ===== B12: EmptyAndWhitespaceOnly_GuardedAsNoSignal =====

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" ")]
    [InlineData("\u200B")]
    public void Should_guard_empty_and_whitespace_as_no_signal(string? raw)
    {
        var q = S(raw);
        Assert.Empty(q.Variants);
        Assert.False(q.HasSignal);
    }

    [Fact]
    public void Should_emit_empty_plan_when_both_fields_empty()
    {
        Assert.Empty(SearchQuerySanitizer.BuildPlan("", "").Tiers);
    }

    // ===== B13: SymbolOnlyTitle_FlaggedNeedsAlias_NotEmptyQuery =====

    [Theory]
    [InlineData("★")]
    [InlineData("†")]
    [InlineData("÷")]
    [InlineData("=")]
    [InlineData("-")]
    [InlineData("?")]
    [InlineData("!!!")]
    [InlineData("/\\/\\/\\Y/\\")]
    [InlineData("O(+>")]
    public void Should_flag_symbol_only_title_as_needs_alias(string raw)
    {
        var q = S(raw);
        Assert.True(q.NeedsAlias, $"'{raw}' should NeedsAlias");
        Assert.DoesNotContain(q.Variants, v => v.Length < 2);
        Assert.DoesNotContain(q.Variants, v => v.Length == 0);
    }

    // ===== B14: ArtistOnlyFallback_AlwaysPresentAndNonTruncated =====

    [Fact]
    public void Should_keep_combined_first_and_full_artist_only_fallback()
    {
        var plan = SearchQuerySanitizer.BuildPlan("Bleu Jeans Bleu", "Record n°V");
        Assert.True(ContainsCI(plan.Tiers[0], "Bleu Jeans Bleu Record nV"));
        Assert.Contains(plan.Tiers.Skip(1), tier => ContainsCI(tier, "Bleu Jeans Bleu"));

        var daft = SearchQuerySanitizer.BuildPlan("Daft Punk", "Discovery");
        Assert.DoesNotContain(daft.Tiers[0], v => string.Equals(v, "Daft Punk", StringComparison.OrdinalIgnoreCase));
    }

    // ===== B15: UrlEncodingRoundTrip_NoUnescapedDelimiter =====

    public static IEnumerable<object[]> UrlHazardCorpus => SearchQueryCorpus.CasesIn(
        "url-plus-space-collision", "url-fragment-delimiter", "url-percent-escape-char",
        "url-query-value-delimiter", "url-query-delimiter", "slash-separator-url-hazard", "url-path-separator");

    [Theory]
    [MemberData(nameof(UrlHazardCorpus))]
    public void Should_round_trip_url_encoding_without_unescaped_delimiters(SearchQueryCase c)
    {
        foreach (var variant in V(c.Raw))
        {
            var encoded = SearchQuerySanitizer.ToQueryParameterValue(variant);
            Assert.DoesNotContain(encoded, ch => ch is '+' or '#' or '?' or '&' or '=');
            Assert.Equal(variant, Uri.UnescapeDataString(encoded));
        }
    }

    [Fact]
    public void Should_encode_plus_as_percent_2B()
    {
        Assert.Equal("%2B", SearchQuerySanitizer.ToQueryParameterValue("+"));
        Assert.DoesNotContain('+', SearchQuerySanitizer.ToQueryParameterValue("Florence + the Machine"));
    }

    // ===== B16: HtmlEntityDecodedBeforeStripping =====

    [Fact]
    public void Should_html_decode_before_stripping()
    {
        var variants = V("Simon &amp; Garfunkel");
        Assert.DoesNotContain(variants, v => Regex.IsMatch(v, @"\bamp\b", RegexOptions.IgnoreCase));
        Assert.True(ContainsCI(variants, "Simon and Garfunkel"));
    }

    // ===== B17: ConnectiveNormalization_AndAmpPlusN =====

    [Theory]
    [InlineData("Simon & Garfunkel", "Simon and Garfunkel")]
    [InlineData("Florence + the Machine", "Florence and the Machine")]
    [InlineData("Guns N' Roses", "Guns and Roses")]
    [InlineData("Belle & Sebastian", "Belle and Sebastian")]
    public void Should_normalize_connectives_to_and(string raw, string spelled)
    {
        Assert.True(ContainsCI(V(raw), spelled), $"'{raw}' missing '{spelled}': [{string.Join(" | ", V(raw))}]");
    }

    // ===== B18: ApostropheClassCanonicalizedThenDeleted =====

    [Fact]
    public void Should_canonicalize_and_delete_apostrophe_class()
    {
        Assert.True(ContainsCI(V("B'Day"), "BDay"));
        Assert.True(ContainsCI(V("Livin' on a Prayer"), "Livin on a Prayer"));
        Assert.True(ContainsCI(V("'Round Midnight"), "Round Midnight"));
        Assert.True(ContainsSubstringCI(V("Israel Kamakawiwoʻole"), "Kamakawiwoole"));
        Assert.True(ContainsCI(V("D'Angelo"), "D Angelo"));

        foreach (var raw in new[] { "B'Day", "Livin' on a Prayer", "'Round Midnight", "D'Angelo", "Guns N' Roses" })
        {
            Assert.All(V(raw), v => Assert.False(v.StartsWith(' ') || v.StartsWith('\'')));
        }
    }

    [Fact]
    public void Should_preserve_apostrophe_original_variant()
    {
        // Regression: the tidal contract PRESERVES the apostrophe in the primary query.
        Assert.Equal("Guns N' Roses", S("Guns N' Roses").Original);
        Assert.True(ContainsCI(V("Guns N' Roses"), "Guns N' Roses"));
    }

    // ===== B19: AlphanumericAndDigitTokens_PreservedNeverSplitOrStripped =====

    [Fact]
    public void Should_preserve_alphanumeric_and_digit_tokens()
    {
        Assert.True(ContainsCI(V("M83"), "M83"));
        Assert.False(ContainsCI(V("M83"), "83"));
        Assert.True(ContainsCI(V("deadmau5"), "deadmau5"));
        Assert.False(ContainsCI(V("deadmau5"), "deadmau"));
        Assert.True(ContainsCI(V("311"), "311"));
        Assert.True(ContainsCI(V("1989"), "1989"));

        var blink = V("blink-182");
        Assert.True(ContainsCI(blink, "blink-182"));
        Assert.True(ContainsCI(blink, "blink 182"));
        Assert.True(ContainsCI(blink, "blink182"));
    }

    // ===== B20: AmbiguousShortTitle_RequiresArtistScope =====

    [Fact]
    public void Should_flag_ambiguous_short_titles_for_artist_scope()
    {
        Assert.True(S("7").RequiresArtistScope);
        Assert.True(S("fun.").RequiresArtistScope);

        var sault = SearchQuerySanitizer.BuildPlan("Sault", "7");
        Assert.DoesNotContain(sault.Tiers, tier => tier.All(v => string.Equals(v, "7", StringComparison.Ordinal)));

        var fun = SearchQuerySanitizer.BuildPlan("fun.", "Some Nights");
        Assert.NotEmpty(fun.Tiers);
        Assert.True(fun.Tiers.Count >= 2); // combined present so "fun" is never the sole signal
    }

    // ===== B21: RomanArabicNumeralCrosswalk (opt-in) =====

    [Fact]
    public void Should_crosswalk_roman_and_arabic_when_enabled()
    {
        var iv = V("Led Zeppelin IV", WithCrosswalk);
        Assert.True(ContainsCI(iv, "Led Zeppelin 4"));
        Assert.True(ContainsCI(iv, "Led Zeppelin IV"));

        Assert.True(ContainsSubstringCI(V("Matchbox Twenty", WithCrosswalk), "Matchbox 20"));
    }

    [Fact]
    public void Should_not_crosswalk_ambiguous_single_roman_letters()
    {
        // Ed Sheeran "X" must not become "10".
        Assert.False(ContainsCI(V("X", WithCrosswalk), "10"));
    }

    // ===== B22: NonLatinScripts_PassThroughNeverDownmappedToSpace =====

    [Theory]
    [InlineData("Молчат Дома")]
    [InlineData("Άννα Βίσση")]
    [InlineData("فيروز")]
    [InlineData("עומר אדם")]
    [InlineData("บอดี้สแลม")]
    [InlineData("अरिजीत सिंह")]
    [InlineData("방탄소년단")]
    [InlineData("夜に駆ける")]
    [InlineData("きゃりーぱみゅぱみゅ")]
    public void Should_pass_through_non_latin_scripts(string raw)
    {
        var q = S(raw);
        Assert.NotEmpty(q.Variants);
        Assert.Equal(raw.Normalize(NormalizationForm.FormC), q.Variants[0]);
        Assert.All(q.Variants, v => Assert.False(string.IsNullOrWhiteSpace(v)));
    }

    // ===== B23: Nfkc_FullwidthAndCompatLigaturesNormalized =====

    [Fact]
    public void Should_normalize_fullwidth_and_compat_ligatures_via_nfkc()
    {
        Assert.True(ContainsSubstringCI(V("マクロスＭＡＣＲＯＳＳ８２－９９"), "MACROSS82-99"));
        Assert.True(ContainsSubstringCI(V("ギミチョコ！！"), "!!"));
        Assert.True(ContainsSubstringCI(V("Paciﬁc 231"), "Pacific"));
        Assert.False(ContainsSubstringCI(V("周杰倫"), "周杰伦")); // NFKC must not bridge Trad↔Simp
    }

    // ===== B24: CjkLetterLikeMarks_ExcludedFromDashAndPunctFolding =====

    [Fact]
    public void Should_preserve_cjk_letter_like_marks()
    {
        Assert.Contains('ー', S("ピースサイン").Original);
        Assert.Contains('ー', S("きゃりーぱみゅぱみゅ").Original);

        var morning = V("モーニング娘。");
        Assert.Contains(morning, v => v.Contains('。'));
        Assert.Contains(morning, v => !v.Contains('。'));
    }

    // ===== B25: CjkPunctuationGlue_DualVariant =====

    [Fact]
    public void Should_emit_dual_variant_for_katakana_middle_dot()
    {
        var variants = V("クリスマス・イブ");
        Assert.True(ContainsCI(variants, "クリスマス イブ"));
        Assert.True(ContainsCI(variants, "クリスマスイブ"));
    }

    // ===== B26: ConfusableHomoglyphFold =====

    [Fact]
    public void Should_fold_confusable_homoglyphs_for_mixed_script()
    {
        Assert.True(ContainsCI(V("KoЯn"), "Korn"));
        Assert.True(ContainsSubstringCI(V("715 - CRΣΣKS"), "CREEKS"));
        Assert.True(ContainsCI(V("ДДТ"), "DDT"));
        Assert.True(ContainsSubstringCI(V("×"), "x"));
    }

    [Fact]
    public void Should_not_fold_wholly_cyrillic_titles()
    {
        // Conflict guard: pure Cyrillic must stay native (Variants[0] == NFC) and not become Latin garbage.
        Assert.Equal("Кино", S("Кино").Variants[0]);
        Assert.Equal("Молчат Дома", S("Молчат Дома").Variants[0]);
    }

    // ===== B27: VersionSuffixStripping_OptionalRankedVariant (opt-in) =====

    [Fact]
    public void Should_strip_version_suffix_as_ranked_variant_when_enabled()
    {
        Assert.True(ContainsCI(V("Take Care (Deluxe)", WithVersionStripping), "Take Care"));
        Assert.True(ContainsCI(V("Easy On Me - Single", WithVersionStripping), "Easy On Me"));
        Assert.True(ContainsCI(V("Uptown Funk (feat. Bruno Mars)", WithVersionStripping), "Uptown Funk"));

        var taylor = V("1989 (Taylor's Version)", WithVersionStripping);
        Assert.True(ContainsSubstringCI(taylor, "Taylors Version"));
        Assert.True(ContainsCI(taylor, "1989"));
        var bareIndex = taylor.ToList().FindIndex(v => string.Equals(v, "1989", StringComparison.OrdinalIgnoreCase));
        var keepIndex = taylor.ToList().FindIndex(v => v.Contains("Taylors Version", StringComparison.OrdinalIgnoreCase));
        Assert.True(bareIndex > keepIndex, "bare '1989' must rank below the discriminator-bearing variant");
    }

    // ===== B28: BracketStripGuard_LeadingAndUnknownParensPreserved (opt-in) =====

    [Fact]
    public void Should_guard_leading_and_unknown_parentheticals()
    {
        var oasis = V("(What's the Story) Morning Glory?", WithVersionStripping);
        Assert.All(oasis, v => Assert.Contains("Story", v, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(oasis, v => string.Equals(v, "Morning Glory", StringComparison.OrdinalIgnoreCase));

        var hurt = V("Hurt (Live at Folsom Prison", WithVersionStripping);
        Assert.True(ContainsCI(hurt, "Hurt"));
        Assert.True(ContainsSubstringCI(hurt, "Folsom"));

        var titanium = V("Titanium (feat. Sia) [Alesso Remix (Radio Edit)]", WithVersionStripping);
        Assert.True(ContainsSubstringCI(titanium, "Remix"));
        Assert.Contains(titanium, v => !v.Contains("Sia", StringComparison.OrdinalIgnoreCase));
    }

    // ===== B29: EmojiAndAstral_StrippedByGraphemeCluster =====

    [Fact]
    public void Should_strip_emoji_and_astral_by_grapheme_cluster()
    {
        Assert.Equal("i love u so much it hurts", S("i love u so much it hurts 💀❤️").Original);
        Assert.Null(Record.Exception(() => Uri.EscapeDataString(S("i love u so much it hurts 💀❤️").Original)));
    }

    // ===== B30: LongTitle_LengthBudgetedBeforeUrl =====

    [Fact]
    public void Should_budget_long_titles_before_url()
    {
        var rows = SearchQueryCorpus.ByCategory("extremely-long-title").ToList();
        var fiona = rows.First(r => r.Raw.Contains("When the Pawn", StringComparison.Ordinal));
        var chumba = rows.First(r => r.Raw.Contains("Boy Bands", StringComparison.Ordinal));

        foreach (var row in new[] { fiona, chumba })
        {
            var variants = V(row.Raw);
            Assert.All(variants, v => Assert.True(v.Length <= SanitizerOptions.Default.MaxLength, $"variant over budget: {v.Length}"));
        }

        Assert.Contains(V(fiona.Raw), v => v.StartsWith("When the Pawn", StringComparison.Ordinal));

        var plan = SearchQuerySanitizer.BuildPlan("Fiona Apple", fiona.Raw);
        Assert.Contains(plan.Tiers.Skip(1), tier => ContainsCI(tier, "Fiona Apple"));
    }

    // ===== B31: Variants_DistinctOrderedAndCaseInsensitiveCompare =====

    [Theory]
    [MemberData(nameof(AllCases))]
    public void Should_keep_variants_distinct_and_original_first(SearchQueryCase c)
    {
        var q = S(c.Raw);
        Assert.Equal(q.Variants.Count, q.Variants.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var withinBudget = q.Original.Length <= SanitizerOptions.Default.MaxLength
            && q.Original.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= SanitizerOptions.Default.MaxTokens;
        if (q.HasSignal && withinBudget)
        {
            Assert.Equal(q.Original, q.Variants[0]);
        }
    }

    [Fact]
    public void Should_compare_equivalent_case_insensitively_without_title_casing()
    {
        Assert.True(SearchQuerySanitizer.AreEquivalent("JAY-Z", "jay-z"));
        Assert.Equal("JAY-Z", S("JAY-Z").Original); // never mutated to title-case
        Assert.True(SearchQuerySanitizer.AreEquivalent("Simon & Garfunkel", "Simon and Garfunkel"));
    }

    // ===== Corpus guard: no silent truncation + category coverage =====

    [Fact]
    public void Corpus_count_matches_embedded_json_element_count()
    {
        var json = EmbeddedJson.ReadAsString("Data/search-query-corpus.json");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(doc.RootElement.GetArrayLength(), SearchQueryCorpus.All.Count);
        Assert.True(SearchQueryCorpus.All.Count >= 239, "corpus shrank below its committed size");
    }

    [Theory]
    [InlineData("degree-ordinal-sign-glued")]
    [InlineData("sanitizer-output-is-raw-not-transport-encoded")]
    [InlineData("lone-surrogate-invalid-utf16")]
    [InlineData("math-alphanumeric-astral-foldable")]
    [InlineData("astral-han-cjk-ext-b")]
    [InlineData("vietnamese-stacked-diacritics")]
    [InlineData("icelandic-thorn-eth-and-extra-nondecomposable-letters")]
    [InlineData("bidi-control-and-isolates")]
    [InlineData("nfkc-singletons")]
    [InlineData("ampersand-inside-initialism")]
    [InlineData("variation-selectors-and-enclosing-keycap")]
    [InlineData("additional-scripts")]
    [InlineData("catalan-middot-gemination")]
    [InlineData("zalgo-combining-mark-storm")]
    [InlineData("classical-catalog-number")]
    [InlineData("classical-movement-title")]
    [InlineData("classical-composer-vs-performer")]
    [InlineData("provider-catalog-slug-divergence")]
    [InlineData("mixed-script-combined")]
    [InlineData("artist-disambiguation-bracket")]
    [InlineData("disc-designation")]
    [InlineData("zwj-round-trip-indic")]
    [InlineData("extended-cyrillic-non-homoglyph")]
    [InlineData("bare-taylors-version")]
    public void Corpus_covers_required_category(string category)
    {
        Assert.NotEmpty(SearchQueryCorpus.ByCategory(category));
    }

    // ===== Fuzz: surrogate / grapheme / combining-storm property test =====

    [Property(MaxTest = 500)]
    public bool Fuzz_arbitrary_utf16_never_throws_and_holds_invariants(ushort[] units)
    {
        var raw = new string((units ?? Array.Empty<ushort>()).Select(u => (char)u).ToArray());
        return HoldsFuzzInvariants(raw);
    }

    [Fact]
    public void Fuzz_lone_surrogates_and_zalgo_never_throw()
    {
        var samples = new[]
        {
            "\uD83D",                       // lone high surrogate
            "\uDE00",                       // lone low surrogate
            "abc\uD83Ddef",                 // lone high surrogate mid-string
            "Be\uD83Dyonce",
            "😀 ok \uDE00",       // valid pair + stray low
            "Z" + new string('\u0301', 200) + "algo", // combining-mark storm
            new string('\u0489', 300),      // combining cyrillic millions sign storm
        };

        foreach (var s in samples)
        {
            Assert.True(HoldsFuzzInvariants(s), $"invariants failed for sample of length {s.Length}");
        }
    }

    private static bool HoldsFuzzInvariants(string raw)
    {
        SanitizedQuery q;
        try
        {
            q = SearchQuerySanitizer.Sanitize(raw);
            SearchQuerySanitizer.BuildPlan(raw, raw);
        }
        catch
        {
            return false;
        }

        if (q.HasSignal && q.Variants.Count == 0)
        {
            return false;
        }

        foreach (var v in q.Variants)
        {
            if (string.IsNullOrWhiteSpace(v))
            {
                return false;
            }

            try
            {
                SearchQuerySanitizer.ToQueryParameterValue(v);
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    private static int TokenCount(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}

/// <summary>
/// Common's own adoption of the shared <see cref="SearchQuerySanitizerParityTestBase"/> — proves the
/// parity axis runs the full corpus against the canonical sanitizer.
/// </summary>
public sealed class CommonSearchQuerySanitizerParityTests : Lidarr.Plugin.Common.TestKit.Compliance.SearchQuerySanitizerParityTestBase
{
    protected override SanitizedQuery SanitizeViaPlugin(string? raw) => SearchQuerySanitizer.Sanitize(raw);

    protected override SearchPlan BuildPlanViaPlugin(string artist, string album) =>
        SearchQuerySanitizer.BuildPlan(artist, album);
}
