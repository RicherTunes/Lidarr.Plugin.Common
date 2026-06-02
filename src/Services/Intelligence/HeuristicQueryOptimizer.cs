using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidarr.Plugin.Common.Services.Intelligence
{
    /// <summary>
    /// Rule-based, dependency-free implementation of <see cref="IQueryOptimizer"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The heuristic core (artist/album feature extraction, the linear
    /// simple/complex scoring with softmax confidence, and the
    /// complexity-driven strategy ordering) is ported from Qobuzarr's
    /// <c>CompiledMLQueryOptimizer</c> — the part of that optimizer that operates
    /// purely on artist/album strings and is therefore service-agnostic.
    /// </para>
    /// <para>
    /// Deliberately <b>not</b> ported: the hybrid / personal-model richness
    /// (ML.NET training, confusion-matrix evaluation, self-tuning thresholds) and
    /// all observability coupling (performance monitors, ML metric collectors,
    /// NLog). Where Qobuzarr's engine returns abstract <i>strategy names</i>
    /// ("exact"/"fuzzy"/"partial"/"keywords"), this implementation maps those
    /// strategies onto the <see cref="IQueryOptimizer"/> contract by emitting
    /// concrete alternative <i>query strings</i> (edition-stripped,
    /// featured-artist-dropped, keyword-reduced) ranked by the same
    /// complexity/confidence signal.
    /// </para>
    /// <para>
    /// The implementation is deterministic and self-contained: no network, no
    /// disk, no host services. An optional <see cref="ILogger"/> may be supplied
    /// for debug tracing; it defaults to a no-op logger.
    /// </para>
    /// </remarks>
    public sealed class HeuristicQueryOptimizer : IQueryOptimizer
    {
        private readonly ILogger _logger;
        private readonly object _metricsLock = new object();

        private long _totalQueries;
        private long _optimizedQueries;
        private double _confidenceSum;
        private long _confidenceSamples;
        private double _relevanceSum;
        private long _relevanceSamples;
        private long _callsSaved;
        private long _callsBaseline;

        // Linear model coefficients ported verbatim from Qobuzarr's
        // CompiledMLQueryOptimizer (16-feature simple/complex weight vectors).
        private static readonly float[] SimpleWeights =
        {
            2.14f, -0.82f, -1.23f, -3.45f, -2.91f, 1.67f, 0.93f, -0.45f,
            -0.62f, -1.84f, -2.31f, -1.95f, -0.77f, -0.54f, 3.21f, -1.43f
        };

        private static readonly float[] ComplexWeights =
        {
            -1.32f, 1.78f, 2.45f, 3.82f, 4.21f, -2.14f, -1.67f, 2.31f,
            2.45f, 3.12f, 3.67f, 2.89f, 1.56f, 2.34f, -2.78f, 3.91f
        };

        // Static decision thresholds (no self-tuning — keeps behaviour
        // deterministic and avoids the ML-drift the source class also disabled).
        private const float SimpleThreshold = 0.65f;
        private const float ComplexThreshold = 0.42f;

        // Heuristic term tables, hoisted so the hot path allocates nothing.
        private static readonly string[] SpecialEditionTerms =
        {
            "remaster", "remastered", "deluxe", "anniversary", "edition",
            "expanded", "collector", "collectors", "special", "bonus",
            "definitive", "super deluxe"
        };

        private static readonly string[] FeaturedArtistPatterns =
        {
            " feat.", " feat ", " ft.", " ft ", " featuring "
        };

        private static readonly string[] CompilationTerms =
        {
            "various", "compilation", "v.a.", "various artists", "va"
        };

        private static readonly string[] LiveAlbumTerms =
        {
            " live", "(live)", "[live]", "concert", "unplugged", "acoustic"
        };

        private static readonly string[] EpOrSingleTerms =
        {
            " ep", "(ep)", "[ep]", "single", "7\"", "12\""
        };

        private static readonly string[] AmbiguousTerms =
        {
            "love", "best", "greatest", "hits", "gold", "collection",
            "the", "new", "first", "one", "two", "three"
        };

        private static readonly Regex NumberedAlbumRegex =
            new Regex(@"^[IVX]+$|^\d+$", RegexOptions.Compiled);

        private static readonly Regex YearInTitleRegex =
            new Regex(@"\b(19|20)\d{2}\b", RegexOptions.Compiled);

        // Strips trailing/embedded "(...)" and "[...]" decoration groups.
        private static readonly Regex BracketGroupRegex =
            new Regex(@"\s*[\(\[][^\(\)\[\]]*[\)\]]", RegexOptions.Compiled);

        private static readonly Regex WhitespaceRegex =
            new Regex(@"\s+", RegexOptions.Compiled);

        /// <summary>
        /// Initialises a new instance of the <see cref="HeuristicQueryOptimizer"/> class.
        /// </summary>
        /// <param name="logger">
        /// Optional logger for debug tracing. Defaults to a no-op logger.
        /// </param>
        public HeuristicQueryOptimizer(ILogger logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        /// <inheritdoc />
        public Task<OptimizedQuery> OptimizeQueryAsync(string originalQuery, QueryContext context = null)
        {
            var primary = NormalizeWhitespace(originalQuery);

            if (string.IsNullOrEmpty(primary))
            {
                lock (_metricsLock)
                {
                    _totalQueries++;
                }

                return Task.FromResult(new OptimizedQuery
                {
                    Query = string.Empty,
                    Confidence = 0.0,
                    Alternatives = new List<string>(),
                    OptimizationReason = "Empty query — nothing to optimize.",
                    IsExperimental = false,
                });
            }

            // Resolve artist/album signal. Prefer structured hints from the
            // caller; otherwise treat the whole query as album-ish text (the
            // artist contributes word-count/specialness signal either way).
            var (artist, album) = ResolveArtistAlbum(primary, context);

            var features = ExtractFeatures(artist, album);
            var complexity = PredictComplexity(features);
            var confidence = ConfidenceFor(features, complexity);

            var alternatives = BuildAlternatives(primary, artist, album, complexity, confidence);

            var optimized = new OptimizedQuery
            {
                Query = primary,
                Confidence = confidence,
                Alternatives = alternatives,
                OptimizationReason = DescribeReason(complexity, alternatives.Count),
                IsExperimental = false,
            };

            lock (_metricsLock)
            {
                _totalQueries++;
                _confidenceSum += confidence;
                _confidenceSamples++;
                if (alternatives.Count > 0)
                {
                    _optimizedQueries++;
                    // Each generated alternative is one fewer blind retry the
                    // caller has to fan out into — a proxy for API calls saved.
                    _callsSaved += alternatives.Count;
                    _callsBaseline += alternatives.Count + 1;
                }
                else
                {
                    _callsBaseline += 1;
                }
            }

            _logger.LogTrace(
                "Optimized query '{Query}' (complexity={Complexity}, confidence={Confidence:F2}, alternatives={Count})",
                primary, complexity, confidence, alternatives.Count);

            return Task.FromResult(optimized);
        }

        /// <inheritdoc />
        public Task LearnFromResultsAsync(string query, QueryResults results, QueryFeedback userFeedback)
        {
            // Best-effort feedback loop. The consumer calls this fire-and-forget
            // off the user-visible path, so it must never throw on hostile input.
            if (results == null)
            {
                return Task.CompletedTask;
            }

            var satisfied = userFeedback?.Satisfied ?? results.HasResults;

            lock (_metricsLock)
            {
                if (satisfied)
                {
                    _relevanceSum += Clamp01(results.RelevanceScore);
                    _relevanceSamples++;
                }
            }

            _logger.LogTrace(
                "Learned from results for '{Query}' (resultCount={Count}, satisfied={Satisfied})",
                query ?? string.Empty, results.ResultCount, satisfied);

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<OptimizationMetrics> GetMetricsAsync()
        {
            lock (_metricsLock)
            {
                var avgConfidence = _confidenceSamples > 0
                    ? _confidenceSum / _confidenceSamples
                    : 0.0;

                var relevanceImprovement = _relevanceSamples > 0
                    ? _relevanceSum / _relevanceSamples
                    : 0.0;

                var apiCallReduction = _callsBaseline > 0
                    ? (double)_callsSaved / _callsBaseline
                    : 0.0;

                return Task.FromResult(new OptimizationMetrics
                {
                    TotalQueries = _totalQueries,
                    OptimizedQueries = _optimizedQueries,
                    AverageConfidence = avgConfidence,
                    ApiCallReduction = apiCallReduction,
                    RelevanceImprovement = relevanceImprovement,
                    TimeRange = TimeSpan.Zero,
                });
            }
        }

        /// <inheritdoc />
        public Task ResetAsync()
        {
            lock (_metricsLock)
            {
                _totalQueries = 0;
                _optimizedQueries = 0;
                _confidenceSum = 0.0;
                _confidenceSamples = 0;
                _relevanceSum = 0.0;
                _relevanceSamples = 0;
                _callsSaved = 0;
                _callsBaseline = 0;
            }

            return Task.CompletedTask;
        }

        // ---- Heuristic core (ported from CompiledMLQueryOptimizer) -------------

        private static (string Artist, string Album) ResolveArtistAlbum(string primary, QueryContext context)
        {
            var artist = TryGetHint(context, "artist");
            var album = TryGetHint(context, "album");

            if (!string.IsNullOrWhiteSpace(artist) || !string.IsNullOrWhiteSpace(album))
            {
                return (artist ?? string.Empty, album ?? primary);
            }

            // No structured hints: the whole query carries the album-ish signal.
            return (string.Empty, primary);
        }

        private static string TryGetHint(QueryContext context, string key)
        {
            if (context?.Metadata == null)
            {
                return null;
            }

            return context.Metadata.TryGetValue(key, out var value)
                ? value as string
                : null;
        }

        /// <summary>
        /// Extracts the 16-feature vector used by the linear model. Ported from
        /// CompiledMLQueryOptimizer.ExtractFeatures.
        /// </summary>
        private static float[] ExtractFeatures(string artistName, string albumTitle)
        {
            var artist = artistName?.ToLowerInvariant() ?? string.Empty;
            var album = albumTitle?.ToLowerInvariant() ?? string.Empty;

            var features = new float[16];

            features[0] = Math.Min(WordCount(artist) / 5.0f, 1.0f);
            features[1] = Math.Min(WordCount(album) / 10.0f, 1.0f);
            features[2] = CountSpecialChars(album) / 5.0f;
            features[3] = (album.Contains("(") || album.Contains("[")) ? 1.0f : 0.0f;
            features[4] = IsSpecialEdition(album) ? 1.0f : 0.0f;
            features[5] = (artist.Length > 0 && !artist.Contains(" ")) ? 1.0f : 0.0f;
            features[6] = IsCommonAlbumPattern(album) ? 1.0f : 0.0f;
            features[7] = Math.Min(album.Length / 50.0f, 1.0f);
            features[8] = HasFeaturedArtists(artist) || HasFeaturedArtists(album) ? 1.0f : 0.0f;
            features[9] = IsCompilation(artist) ? 1.0f : 0.0f;
            features[10] = HasYearInTitle(album) ? 1.0f : 0.0f;
            features[11] = IsLiveAlbum(album) ? 1.0f : 0.0f;
            features[12] = IsEpOrSingle(album) ? 1.0f : 0.0f;
            features[13] = HasNonAsciiChars(artist + " " + album) ? 1.0f : 0.0f;
            features[14] = CalculateStringSimilarity(artist, album);
            features[15] = CalculateAmbiguityScore(artist, album);

            return features;
        }

        private static float ComputeScore(float[] features, float[] weights)
        {
            float score = 0;
            var n = Math.Min(features.Length, weights.Length);
            for (var i = 0; i < n; i++)
            {
                score += features[i] * weights[i];
            }

            return score;
        }

        private static QueryComplexityLevel PredictComplexity(float[] features)
        {
            var simpleScore = ComputeScore(features, SimpleWeights);
            var complexScore = ComputeScore(features, ComplexWeights);

            if (simpleScore > SimpleThreshold && simpleScore > complexScore)
            {
                return QueryComplexityLevel.Simple;
            }

            if (complexScore > ComplexThreshold)
            {
                return QueryComplexityLevel.Complex;
            }

            return QueryComplexityLevel.Medium;
        }

        /// <summary>
        /// Softmax-style confidence for the chosen complexity. Ported from
        /// CompiledMLQueryOptimizer.GetConfidenceScore.
        /// </summary>
        private static double ConfidenceFor(float[] features, QueryComplexityLevel complexity)
        {
            var simpleScore = Math.Exp(ComputeScore(features, SimpleWeights));
            var complexScore = Math.Exp(ComputeScore(features, ComplexWeights));
            var mediumScore = Math.Exp(1.0);

            var total = simpleScore + complexScore + mediumScore;
            if (total <= 0)
            {
                return 0.33;
            }

            switch (complexity)
            {
                case QueryComplexityLevel.Simple:
                    return Clamp01(simpleScore / total);
                case QueryComplexityLevel.Complex:
                    return Clamp01(complexScore / total);
                case QueryComplexityLevel.Medium:
                    return Clamp01(mediumScore / total);
                default:
                    return 0.33;
            }
        }

        // ---- Strategy -> concrete alternative query strings -------------------

        /// <summary>
        /// Maps the complexity-driven strategy ordering (the analogue of
        /// CompiledMLQueryOptimizer.GetOptimizedQueryStrategies) onto concrete
        /// alternative query strings, ranked best-first.
        /// </summary>
        private static List<string> BuildAlternatives(
            string primary,
            string artist,
            string album,
            QueryComplexityLevel complexity,
            double confidence)
        {
            var order = StrategyOrder(complexity, confidence);
            var strippedBase = BuildStrippedBase(primary, artist, album);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primary };
            var result = new List<string>();

            foreach (var strategy in order)
            {
                var candidate = ApplyStrategy(strategy, strippedBase);
                if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
                {
                    result.Add(candidate);
                }
            }

            return result;
        }

        /// <summary>
        /// Strategy ordering ported from GetOptimizedQueryStrategies: high
        /// confidence uses a complexity-targeted order, low confidence broadens.
        /// </summary>
        private static IReadOnlyList<QueryStrategy> StrategyOrder(QueryComplexityLevel complexity, double confidence)
        {
            if (confidence > 0.7)
            {
                switch (complexity)
                {
                    case QueryComplexityLevel.Simple:
                        return new[] { QueryStrategy.Exact, QueryStrategy.Fuzzy, QueryStrategy.Partial };
                    case QueryComplexityLevel.Complex:
                        return new[] { QueryStrategy.Partial, QueryStrategy.Keywords, QueryStrategy.Fuzzy, QueryStrategy.Exact };
                    case QueryComplexityLevel.Medium:
                        return new[] { QueryStrategy.Fuzzy, QueryStrategy.Partial, QueryStrategy.Exact, QueryStrategy.Keywords };
                }
            }

            return new[] { QueryStrategy.Fuzzy, QueryStrategy.Partial, QueryStrategy.Exact, QueryStrategy.Keywords };
        }

        /// <summary>
        /// Turns a strategy into a concrete alternative query string. Each
        /// strategy transforms a common cleaned <paramref name="baseQuery"/>;
        /// a transform that finds nothing to do returns the base unchanged, and
        /// <see cref="BuildAlternatives"/> dedupes anything equal to the primary
        /// (or to an earlier alternative). This keeps composition robust: a
        /// no-op inner step never collapses the candidate to empty.
        /// </summary>
        private static string ApplyStrategy(QueryStrategy strategy, string baseQuery)
        {
            switch (strategy)
            {
                case QueryStrategy.Exact:
                    // The faithful, fully-cleaned form — equals the primary when
                    // the input carried no decorations, so it dedupes away.
                    return baseQuery;

                case QueryStrategy.Partial:
                    // The decoration-stripped base is the partial form.
                    return baseQuery;

                case QueryStrategy.Fuzzy:
                    // Drop featured-artist clauses (catalogue attribution differs).
                    return DropFeaturedArtists(baseQuery);

                case QueryStrategy.Keywords:
                    // Reduce to the most salient tokens, dropping ambiguous filler.
                    return ReduceToKeywords(baseQuery);

                default:
                    return baseQuery;
            }
        }

        /// <summary>
        /// Builds the decoration-stripped base query that every strategy starts
        /// from. When the caller supplied a structured album hint, the edition
        /// noise is stripped from that field and recombined with the artist;
        /// otherwise parenthetical/bracketed groups are stripped from the query.
        /// Returns the primary unchanged when there is nothing to strip.
        /// </summary>
        private static string BuildStrippedBase(string primary, string artist, string album)
        {
            if (!string.IsNullOrWhiteSpace(album))
            {
                var cleanedAlbum = NormalizeWhitespace(BracketGroupRegex.Replace(album, string.Empty));
                if (!string.IsNullOrWhiteSpace(cleanedAlbum))
                {
                    var prefix = string.IsNullOrWhiteSpace(artist) ? string.Empty : artist.Trim() + " ";
                    var rebuilt = NormalizeWhitespace(prefix + cleanedAlbum);
                    if (!string.IsNullOrWhiteSpace(rebuilt))
                    {
                        return rebuilt;
                    }
                }
            }

            var stripped = NormalizeWhitespace(BracketGroupRegex.Replace(primary, string.Empty));
            return string.IsNullOrWhiteSpace(stripped) ? primary : stripped;
        }

        private static string DropFeaturedArtists(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return string.Empty;
            }

            foreach (var pattern in FeaturedArtistPatterns)
            {
                var idx = query.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    // Drop the featured clause and everything up to it on the left
                    // (the lead artist precedes "feat."); keep the work title that
                    // follows the featured guest's name.
                    var head = NormalizeWhitespace(query.Substring(0, idx));
                    var tail = query.Substring(idx + pattern.Length);
                    var titleTail = TitleAfterFeaturedGuest(tail);
                    var rejoined = NormalizeWhitespace(head + " " + titleTail);
                    return string.IsNullOrWhiteSpace(rejoined) ? head : rejoined;
                }
            }

            // No featured clause: nothing to drop, return the base unchanged.
            return query;
        }

        /// <summary>
        /// Given the text following a "feat."-style marker (e.g.
        /// "Rihanna Love The Way You Lie"), drops the guest artist token(s) and
        /// returns the remaining work title. Heuristic: the guest is the leading
        /// capitalised run; the title is whatever follows once a lowercase/common
        /// word appears. Falls back to dropping just the first token.
        /// </summary>
        private static string TitleAfterFeaturedGuest(string tail)
        {
            var tokens = tail.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length <= 1)
            {
                return string.Empty;
            }

            // Drop the first token (the guest's primary name); keep the rest.
            return string.Join(" ", tokens.Skip(1));
        }

        private static string ReduceToKeywords(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return string.Empty;
            }

            var tokens = query
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => !AmbiguousTerms.Contains(t, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (tokens.Count == 0)
            {
                // Everything was filler — keep the base rather than emit nothing.
                return query;
            }

            return NormalizeWhitespace(string.Join(" ", tokens));
        }

        private static string DescribeReason(QueryComplexityLevel complexity, int alternativeCount)
        {
            if (alternativeCount == 0)
            {
                return $"Query classified {complexity}; already minimal — no rewrites generated.";
            }

            return $"Query classified {complexity}; generated {alternativeCount} ranked fallback query/queries.";
        }

        // ---- Feature helpers (ported) -----------------------------------------

        private static int WordCount(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            return text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private static int CountSpecialChars(string text)
        {
            return text.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c));
        }

        private static bool IsSpecialEdition(string album)
        {
            return SpecialEditionTerms.Any(term => album.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsCommonAlbumPattern(string album)
        {
            if (album.Length < 20 && !album.Contains("(") && !album.Contains("["))
            {
                return true;
            }

            if (album.Equals("self-titled", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return NumberedAlbumRegex.IsMatch(album);
        }

        private static bool HasFeaturedArtists(string text)
        {
            return FeaturedArtistPatterns.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase))
                   || text.Contains(" & ", StringComparison.OrdinalIgnoreCase)
                   || text.Contains(" with ", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCompilation(string artist)
        {
            return CompilationTerms.Any(term => artist.Equals(term, StringComparison.OrdinalIgnoreCase))
                   || artist.Contains("various artists", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasYearInTitle(string album)
        {
            return YearInTitleRegex.IsMatch(album);
        }

        private static bool IsLiveAlbum(string album)
        {
            return LiveAlbumTerms.Any(term => album.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsEpOrSingle(string album)
        {
            return EpOrSingleTerms.Any(term => album.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasNonAsciiChars(string text)
        {
            return text.Any(c => c > 127);
        }

        private static float CalculateStringSimilarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
            {
                return 0f;
            }

            var words1 = s1.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var words2 = s2.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet();

            if (words1.Count == 0 || words2.Count == 0)
            {
                return 0f;
            }

            var intersection = words1.Intersect(words2).Count();
            var union = words1.Union(words2).Count();

            return union > 0 ? (float)intersection / union : 0f;
        }

        private static float CalculateAmbiguityScore(string artist, string album)
        {
            var words = (artist + " " + album).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var ambiguousCount = words.Count(w => AmbiguousTerms.Contains(w, StringComparer.OrdinalIgnoreCase));

            return Math.Min(ambiguousCount / 3.0f, 1.0f);
        }

        // ---- Small utilities ---------------------------------------------------

        private static string NormalizeWhitespace(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            return WhitespaceRegex.Replace(input, " ").Trim();
        }

        private static double Clamp01(double value)
        {
            if (double.IsNaN(value))
            {
                return 0.0;
            }

            if (value < 0.0)
            {
                return 0.0;
            }

            return value > 1.0 ? 1.0 : value;
        }

        /// <summary>
        /// Internal complexity bucket — the Common analogue of Qobuzarr's
        /// <c>QueryComplexity</c> enum. Kept private so it does not leak into the
        /// public surface (the contract speaks in <see cref="OptimizedQuery"/>).
        /// </summary>
        private enum QueryComplexityLevel
        {
            Simple,
            Medium,
            Complex,
        }

        /// <summary>
        /// Internal search-strategy label — the Common analogue of the
        /// strategy-name strings Qobuzarr's engine returns.
        /// </summary>
        private enum QueryStrategy
        {
            Exact,
            Partial,
            Fuzzy,
            Keywords,
        }
    }
}
