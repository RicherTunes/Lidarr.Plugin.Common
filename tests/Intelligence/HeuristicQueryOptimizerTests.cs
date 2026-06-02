using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Intelligence;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Intelligence
{
    /// <summary>
    /// Behavioural pins for <see cref="HeuristicQueryOptimizer"/>, the Common
    /// rule-based implementation of <see cref="IQueryOptimizer"/>. The generic
    /// heuristic core (artist/album feature extraction + linear complexity
    /// scoring + strategy-ordered alternative generation) is ported from
    /// Qobuzarr's CompiledMLQueryOptimizer, decoupled from any observability.
    /// </summary>
    [Trait("Category", "Unit")]
    public class HeuristicQueryOptimizerTests
    {
        private static HeuristicQueryOptimizer NewOptimizer() => new HeuristicQueryOptimizer();

        #region OptimizeQueryAsync — primary query

        [Fact]
        public async Task OptimizeQueryAsync_ReturnsNonNullResultWithPrimaryQuery()
        {
            var optimizer = NewOptimizer();

            var result = await optimizer.OptimizeQueryAsync("Daft Punk Discovery");

            Assert.NotNull(result);
            Assert.False(string.IsNullOrWhiteSpace(result.Query));
        }

        [Fact]
        public async Task OptimizeQueryAsync_TrimsAndCollapsesWhitespaceInPrimaryQuery()
        {
            var optimizer = NewOptimizer();

            var result = await optimizer.OptimizeQueryAsync("  Daft   Punk    Discovery  ");

            Assert.Equal("Daft Punk Discovery", result.Query);
        }

        [Fact]
        public async Task OptimizeQueryAsync_PrimaryQueryPreservedForSimpleQuery()
        {
            var optimizer = NewOptimizer();

            var result = await optimizer.OptimizeQueryAsync("Adele 21");

            // A short, clean query is "simple": the primary stays faithful to the input.
            Assert.Equal("Adele 21", result.Query);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t")]
        public async Task OptimizeQueryAsync_EmptyOrWhitespaceQuery_ReturnsEmptyPrimaryAndNoThrow(string? query)
        {
            var optimizer = NewOptimizer();

            var result = await optimizer.OptimizeQueryAsync(query!);

            Assert.NotNull(result);
            Assert.Equal(string.Empty, result.Query);
            Assert.Empty(result.Alternatives);
            // No optimization is possible/meaningful for an empty input.
            Assert.False(result.IsExperimental);
        }

        [Fact]
        public async Task OptimizeQueryAsync_NullContext_DoesNotThrow()
        {
            var optimizer = NewOptimizer();

            var result = await optimizer.OptimizeQueryAsync("Radiohead OK Computer", context: null!);

            Assert.NotNull(result);
            Assert.Equal("Radiohead OK Computer", result.Query);
        }

        #endregion

        #region OptimizeQueryAsync — confidence

        [Fact]
        public async Task OptimizeQueryAsync_ConfidenceIsWithinUnitInterval()
        {
            var optimizer = NewOptimizer();

            var result = await optimizer.OptimizeQueryAsync("Miles Davis Kind of Blue (Legacy Edition) [Remastered]");

            Assert.InRange(result.Confidence, 0.0, 1.0);
        }

        [Fact]
        public async Task OptimizeQueryAsync_ComplexQueryYieldsMoreAlternativesThanSimpleQuery()
        {
            var optimizer = NewOptimizer();

            var simple = await optimizer.OptimizeQueryAsync("Adele 21");
            var complex = await optimizer.OptimizeQueryAsync(
                "Various Artists Now That's What I Call Music 1999 (Deluxe Anniversary Edition) [Remastered] (Live)");

            // The whole point of optimization is to spend more fallback queries
            // on hard inputs: a short clean query needs no rewrites, whereas an
            // edition-laden, ambiguous one yields several ranked alternatives.
            Assert.Empty(simple.Alternatives);
            Assert.True(
                complex.Alternatives.Count > simple.Alternatives.Count,
                $"expected complex alternatives ({complex.Alternatives.Count}) > simple ({simple.Alternatives.Count})");
        }

        #endregion

        #region OptimizeQueryAsync — alternatives

        [Fact]
        public async Task OptimizeQueryAsync_ComplexQueryProducesRankedAlternatives()
        {
            var optimizer = NewOptimizer();

            var result = await optimizer.OptimizeQueryAsync(
                "Pink Floyd The Dark Side of the Moon (50th Anniversary Edition) [Remastered]");

            // A complex, edition-laden query should yield at least one cheaper
            // alternative (e.g. the edition markers stripped).
            Assert.NotEmpty(result.Alternatives);
        }

        [Fact]
        public async Task OptimizeQueryAsync_AlternativesAreDistinctAndExcludeThePrimary()
        {
            var optimizer = NewOptimizer();

            var result = await optimizer.OptimizeQueryAsync(
                "Beck Sea Change (Deluxe Edition) [Remastered]");

            Assert.DoesNotContain(result.Query, result.Alternatives);
            Assert.Equal(result.Alternatives.Count, result.Alternatives.Distinct().Count());
            Assert.DoesNotContain(result.Alternatives, string.IsNullOrWhiteSpace);
        }

        [Fact]
        public async Task OptimizeQueryAsync_StripsParentheticalEditionMarkersIntoAnAlternative()
        {
            var optimizer = NewOptimizer();

            var result = await optimizer.OptimizeQueryAsync(
                "Radiohead OK Computer (OKNOTOK 1997 2017) [Deluxe Edition]");

            // The "strip edition decorations" alternative is the highest-value
            // reduction the heuristic core knows how to make.
            Assert.Contains("Radiohead OK Computer", result.Alternatives);
        }

        [Fact]
        public async Task OptimizeQueryAsync_DropsFeaturedArtistClauseIntoAnAlternative()
        {
            var optimizer = NewOptimizer();

            var result = await optimizer.OptimizeQueryAsync("Eminem feat. Rihanna Love The Way You Lie");

            // Featured-artist clauses frequently differ between catalogues, so a
            // de-featured alternative is generated.
            Assert.Contains(result.Alternatives, a => !a.Contains("feat", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task OptimizeQueryAsync_SpecialCharactersDoNotThrowAndAreReflectedInAlternatives()
        {
            var optimizer = NewOptimizer();

            var result = await optimizer.OptimizeQueryAsync("Sigur Rós ( ) [Untitled] / Vaka?");

            Assert.NotNull(result);
            Assert.False(string.IsNullOrWhiteSpace(result.Query));
            // Non-ASCII and punctuation are handled without throwing.
            Assert.All(result.Alternatives, a => Assert.False(string.IsNullOrWhiteSpace(a)));
        }

        [Fact]
        public async Task OptimizeQueryAsync_UsesArtistAndAlbumHintsFromContextMetadata()
        {
            var optimizer = NewOptimizer();
            var context = new QueryContext
            {
                Type = QueryType.Album,
                Metadata = new Dictionary<string, object>
                {
                    ["artist"] = "The Beatles",
                    ["album"] = "Abbey Road (Super Deluxe Edition)"
                }
            };

            var result = await optimizer.OptimizeQueryAsync("The Beatles Abbey Road (Super Deluxe Edition)", context);

            // When the caller supplies structured artist/album hints, the
            // edition-stripped alternative is derived from the album field.
            Assert.Contains(result.Alternatives, a => a.Contains("Abbey Road", StringComparison.OrdinalIgnoreCase)
                                                      && !a.Contains("Deluxe", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task OptimizeQueryAsync_OptimizationReasonIsPopulated()
        {
            var optimizer = NewOptimizer();

            var result = await optimizer.OptimizeQueryAsync("Tool Lateralus");

            Assert.False(string.IsNullOrWhiteSpace(result.OptimizationReason));
        }

        #endregion

        #region Metrics + learning

        [Fact]
        public async Task GetMetricsAsync_OnFreshOptimizer_ReturnsZeroedMetrics()
        {
            var optimizer = NewOptimizer();

            var metrics = await optimizer.GetMetricsAsync();

            Assert.NotNull(metrics);
            Assert.Equal(0, metrics.TotalQueries);
            Assert.Equal(0, metrics.OptimizedQueries);
        }

        [Fact]
        public async Task OptimizeQueryAsync_IncrementsTotalQueriesMetric()
        {
            var optimizer = NewOptimizer();

            await optimizer.OptimizeQueryAsync("Portishead Dummy");
            await optimizer.OptimizeQueryAsync("Massive Attack Mezzanine");

            var metrics = await optimizer.GetMetricsAsync();

            Assert.Equal(2, metrics.TotalQueries);
        }

        [Fact]
        public async Task OptimizeQueryAsync_CountsQueriesItActuallyRewroteAsOptimized()
        {
            var optimizer = NewOptimizer();

            // Edition-laden query => an alternative is produced => counts as optimized.
            await optimizer.OptimizeQueryAsync("Wilco Yankee Hotel Foxtrot (Deluxe Edition) [Remastered]");
            // Trivial clean query => primary unchanged, no alternatives => not optimized.
            await optimizer.OptimizeQueryAsync("Air Moon Safari");

            var metrics = await optimizer.GetMetricsAsync();

            Assert.Equal(2, metrics.TotalQueries);
            Assert.Equal(1, metrics.OptimizedQueries);
        }

        [Fact]
        public async Task LearnFromResultsAsync_DoesNotThrowAndIsReflectedInMetrics()
        {
            var optimizer = NewOptimizer();
            var opt = await optimizer.OptimizeQueryAsync("Boards of Canada Geogaddi");

            var results = new QueryResults
            {
                ResultCount = 5,
                ExecutionTime = TimeSpan.FromMilliseconds(120),
                RelevanceScore = 0.9
            };
            var feedback = new QueryFeedback { Satisfied = true, Rating = 5 };

            await optimizer.LearnFromResultsAsync(opt.Query, results, feedback);

            var metrics = await optimizer.GetMetricsAsync();
            // Relevance improvement is an average of the satisfied learning signals.
            Assert.InRange(metrics.RelevanceImprovement, 0.0, 1.0);
        }

        [Fact]
        public async Task LearnFromResultsAsync_NullArguments_DoNotThrow()
        {
            var optimizer = NewOptimizer();

            // Best-effort feedback loop: hostile/empty inputs must never throw,
            // mirroring the consumer's fire-and-forget call site.
            await optimizer.LearnFromResultsAsync(null!, null!, null!);
            await optimizer.LearnFromResultsAsync(string.Empty, new QueryResults(), new QueryFeedback());

            var metrics = await optimizer.GetMetricsAsync();
            Assert.NotNull(metrics);
        }

        [Fact]
        public async Task AverageConfidence_TracksTheConfidenceOfOptimizedQueries()
        {
            var optimizer = NewOptimizer();

            await optimizer.OptimizeQueryAsync("Aphex Twin Selected Ambient Works 85-92");
            await optimizer.OptimizeQueryAsync("Burial Untrue");

            var metrics = await optimizer.GetMetricsAsync();

            Assert.InRange(metrics.AverageConfidence, 0.0, 1.0);
        }

        #endregion

        #region Reset

        [Fact]
        public async Task ResetAsync_ClearsAccumulatedMetrics()
        {
            var optimizer = NewOptimizer();
            await optimizer.OptimizeQueryAsync("Four Tet Rounds");
            await optimizer.OptimizeQueryAsync("Caribou Swim (Deluxe Edition)");

            await optimizer.ResetAsync();

            var metrics = await optimizer.GetMetricsAsync();
            Assert.Equal(0, metrics.TotalQueries);
            Assert.Equal(0, metrics.OptimizedQueries);
            Assert.Equal(0.0, metrics.AverageConfidence);
        }

        #endregion

        #region Determinism

        [Fact]
        public async Task OptimizeQueryAsync_IsDeterministicForTheSameInput()
        {
            var optimizer = NewOptimizer();

            var first = await optimizer.OptimizeQueryAsync("New Order Power, Corruption & Lies (Definitive Edition)");
            var second = await NewOptimizer().OptimizeQueryAsync("New Order Power, Corruption & Lies (Definitive Edition)");

            Assert.Equal(first.Query, second.Query);
            Assert.Equal(first.Alternatives, second.Alternatives);
            Assert.Equal(first.Confidence, second.Confidence, precision: 10);
        }

        #endregion
    }
}
